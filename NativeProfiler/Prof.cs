using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.LowLevel;

namespace NativePatchProfiler
{
    /// <summary>
    /// Lightweight main-thread section timer injected into assembly_valheim.dll by the patcher (--profile).
    /// Writes slow-frame breakdowns and a per-minute summary to native_profile.log next to Player.log.
    /// </summary>
    public static class Prof
    {
        const int Cap = 4096;
        const double SlowFrameMs = 33.0;   // below 30 fps for a single frame (with vsync: one missed refresh)
        const double HitchMs = 50.0;       // three or more refreshes: a real stall, always worth a line
        const double SpikeMs = 4.0;        // a single section eating 4 ms is worth counting
        const double WindowSeconds = 60.0;
        // Missed-refresh frames are frequent and alike, so they only get a sample; hitches get their own budget
        const int MaxSlowLinesPerWindow = 10;
        const int MaxHitchLinesPerWindow = 60;

        static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

        static readonly string[] s_names = new string[Cap];
        static readonly bool[] s_nested = new bool[Cap];
        static readonly bool[] s_loop = new bool[Cap];   // Unity PlayerLoop phase: engine time, overlaps script sections
        static readonly Dictionary<string, int> s_scopeIds = new Dictionary<string, int>();
        static int s_nextScopeId = Cap - 1;

        // Current frame
        static readonly long[] s_frameTicks = new long[Cap];
        static readonly bool[] s_touched = new bool[Cap];
        static readonly int[] s_touchedList = new int[Cap];
        static int s_touchedCount;

        // Current summary window
        static readonly long[] s_winTicks = new long[Cap];
        static readonly long[] s_winMaxFrame = new long[Cap];
        static readonly int[] s_winCalls = new int[Cap];
        static readonly int[] s_winSpikes = new int[Cap];
        static long s_winFrames, s_winFrameTicks, s_winFrameMax, s_winStart;
        static int s_winSlow, s_winHitches, s_winGcs, s_winSlowLogged, s_winHitchLogged;

        static long s_lastFrame;
        static int s_lastGcCount;
        static StreamWriter s_log;

        // PlayerLoop markers: time between two markers is charged to the subsystem that ran in between
        static bool s_loopInstalled;
        static int s_loopOpen = -1;
        static long s_loopMark;

        // Engine counters (render stats are available in release players; frame timing only if enabled in the build)
        static readonly Counter[] s_counters =
        {
            new Counter(ProfilerCategory.Render, "Draw Calls Count", "draws", 1),
            new Counter(ProfilerCategory.Render, "SetPass Calls Count", "setpass", 1),
            new Counter(ProfilerCategory.Render, "Shadow Casters Count", "shadowcasters", 1),
            new Counter(ProfilerCategory.Render, "Triangles Count", "ktris", 1000),
            new Counter(ProfilerCategory.Internal, "GPU Frame Time", "gpu_ms", 1000000),
            new Counter(ProfilerCategory.Internal, "CPU Render Thread Frame Time", "renderthread_ms", 1000000),
            new Counter(ProfilerCategory.Memory, "System Used Memory", "mem_mb", 1024 * 1024),
        };

        sealed class Counter
        {
            public readonly string Label;
            readonly long m_scale;
            ProfilerRecorder m_rec;
            public long Last, WinSum, WinMax;

            public Counter(ProfilerCategory category, string stat, string label, long scale)
            {
                Label = label;
                m_scale = scale;
                try { m_rec = ProfilerRecorder.StartNew(category, stat); } catch { }
            }

            public bool Valid => m_rec.Valid;

            public void Sample()
            {
                if (!m_rec.Valid) return;
                Last = m_rec.LastValue;
                WinSum += Last;
                if (Last > WinMax) WinMax = Last;
            }

            public string Format(long v) => m_scale == 1 ? v.ToString() : ((double)v / m_scale).ToString("0.0");
        }

        /// <summary>Section names separated by '|'; a leading '>' marks a section nested inside another one.</summary>
        public static void Init(string names)
        {
            if (s_names[0] != null) return;
            string[] parts = names.Split('|');
            for (int i = 0; i < parts.Length && i < s_nextScopeId; i++)
            {
                bool nested = parts[i].StartsWith(">", StringComparison.Ordinal);
                s_nested[i] = nested;
                s_names[i] = nested ? parts[i].Substring(1) : parts[i];
            }
            try
            {
                InstallLoopMarkers();
            }
            catch (Exception ex)
            {
                Write("profiler: PlayerLoop markers not installed: " + ex.Message);
            }
            Application.quitting += OnQuitting;
        }

        /// <summary>Writes the unfinished window and flushes, so the log does not end mid-line.</summary>
        static void OnQuitting()
        {
            try
            {
                if (s_lastFrame != 0) WriteSummary(s_lastFrame, partial: true);
                s_log?.Flush();
            }
            catch
            {
                // Never let the profiler break the game
            }
        }

        public static long Begin() => Stopwatch.GetTimestamp();

        public static void End(int id, long start)
        {
            long dt = Stopwatch.GetTimestamp() - start;
            if ((uint)id >= Cap) return;
            Add(id, dt);
        }

        /// <summary>For MonoUpdaters batches, keyed by the game's own profiler scope string.</summary>
        public static void EndScope(string scope, long start)
        {
            long dt = Stopwatch.GetTimestamp() - start;
            if (scope == null) return;
            int id = ScopeId(scope);
            if (id < 0) return;
            s_nested[id] = true; // always runs inside MonoUpdaters.Update/FixedUpdate/LateUpdate
            Add(id, dt);
        }

        static int ScopeId(string name)
        {
            if (!s_scopeIds.TryGetValue(name, out int id))
            {
                if (s_nextScopeId <= 0) return -1;
                id = s_nextScopeId--;
                s_scopeIds[name] = id;
                s_names[id] = name;
            }
            return id;
        }

        static void Add(int id, long dt)
        {
            if (!s_touched[id])
            {
                s_touched[id] = true;
                s_touchedList[s_touchedCount++] = id;
            }
            s_frameTicks[id] += dt;
            s_winTicks[id] += dt;
            s_winCalls[id]++;
        }

        static void InstallLoopMarkers()
        {
            PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem[] phases = root.subSystemList;
            if (phases == null || phases.Length == 0) return;
            for (int p = 0; p < phases.Length; p++)
            {
                PlayerLoopSystem[] subs = phases[p].subSystemList;
                if (subs == null || subs.Length == 0) continue;
                string phase = phases[p].type?.Name ?? "Phase" + p;
                var list = new List<PlayerLoopSystem>(subs.Length * 2 + 1);
                for (int i = 0; i < subs.Length; i++)
                {
                    list.Add(Marker(LoopId(phase + "." + (subs[i].type?.Name ?? i.ToString())), frameStart: p == 0 && i == 0));
                    list.Add(subs[i]);
                }
                // Whatever runs after the phase and before the next marker (outside the loop: OS/present wait)
                list.Add(Marker(LoopId("after " + phase), frameStart: false));
                phases[p].subSystemList = list.ToArray();
            }
            root.subSystemList = phases;
            PlayerLoop.SetPlayerLoop(root);
            s_loopInstalled = true;
        }

        static int LoopId(string name)
        {
            int id = ScopeId(name);
            if (id >= 0) s_loop[id] = true;
            return id;
        }

        static PlayerLoopSystem Marker(int id, bool frameStart) => new PlayerLoopSystem
        {
            type = typeof(Prof),
            updateDelegate = () => Mark(id, frameStart),
        };

        static void Mark(int next, bool frameStart)
        {
            long now = Stopwatch.GetTimestamp();
            if (s_loopOpen >= 0) Add(s_loopOpen, now - s_loopMark);
            if (frameStart) FrameSafe(now);
            s_loopOpen = next;
            s_loopMark = Stopwatch.GetTimestamp();
        }

        /// <summary>Called once per frame at the start of Game.Update; superseded by the PlayerLoop frame marker.</summary>
        public static void Frame()
        {
            if (s_loopInstalled) return;
            FrameSafe(Stopwatch.GetTimestamp());
        }

        static void FrameSafe(long now)
        {
            try
            {
                FrameCore(now);
            }
            catch (Exception ex)
            {
                // Never let the profiler break the game
                Write("profiler error: " + ex.Message);
            }
        }

        static void FrameCore(long now)
        {
            if (s_lastFrame == 0)
            {
                ResetFrame();
                ResetWindow(now);
                s_lastFrame = now;
                s_lastGcCount = GC.CollectionCount(0);
                Write($"=== Valheim native profiler started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (slow frame >= {SlowFrameMs} ms, spike >= {SpikeMs} ms)");
                Write("    PlayerLoop phases: " + (s_loopInstalled ? "on" : "off") + ", counters: " +
                      string.Join(", ", s_counters.Select(c => c.Label + (c.Valid ? "" : " (n/a)"))));
                return;
            }

            long frameTicks = now - s_lastFrame;
            s_lastFrame = now;
            s_winFrames++;
            s_winFrameTicks += frameTicks;
            if (frameTicks > s_winFrameMax) s_winFrameMax = frameTicks;

            // Counters hold the last completed frame's values
            for (int i = 0; i < s_counters.Length; i++) s_counters[i].Sample();

            int gcCount = GC.CollectionCount(0);
            bool gcThisFrame = gcCount != s_lastGcCount;
            s_lastGcCount = gcCount;
            if (gcThisFrame) s_winGcs++;

            double frameMs = frameTicks * s_tickToMs;
            if (frameMs >= HitchMs)
            {
                s_winSlow++;
                s_winHitches++;
                if (s_winHitchLogged < MaxHitchLinesPerWindow)
                {
                    s_winHitchLogged++;
                    LogSlowFrame("HITCH", frameMs, frameTicks, gcThisFrame);
                    s_log?.Flush();
                }
            }
            else if (frameMs >= SlowFrameMs)
            {
                s_winSlow++;
                if (s_winSlowLogged < MaxSlowLinesPerWindow)
                {
                    s_winSlowLogged++;
                    LogSlowFrame("SLOW ", frameMs, frameTicks, gcThisFrame);
                }
            }

            for (int i = 0; i < s_touchedCount; i++)
            {
                int id = s_touchedList[i];
                long t = s_frameTicks[id];
                if (t > s_winMaxFrame[id]) s_winMaxFrame[id] = t;
                if (t * s_tickToMs >= SpikeMs) s_winSpikes[id]++;
            }
            ResetFrame();

            if ((now - s_winStart) * s_tickToMs >= WindowSeconds * 1000.0)
            {
                WriteSummary(now, partial: false);
                ResetWindow(now);
                // The shadow census can take tens of ms: keep it out of the next frame's time
                s_lastFrame = Stopwatch.GetTimestamp();
            }
        }

        static void LogSlowFrame(string kind, double frameMs, long frameTicks, bool gc)
        {
            long topLevel = 0;
            for (int i = 0; i < s_touchedCount; i++)
            {
                int id = s_touchedList[i];
                if (!s_nested[id] && !s_loop[id]) topLevel += s_frameTicks[id];
            }
            var ids = Enumerable.Range(0, s_touchedCount).Select(i => s_touchedList[i]).OrderByDescending(id => s_frameTicks[id]).ToList();
            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss} {kind} {frameMs,6:0.0} ms{(gc ? " [GC]" : "")}{CameraPos()} | scripts {topLevel * s_tickToMs:0.0} | engine/render/wait {(frameTicks - topLevel) * s_tickToMs:0.0}");
            foreach (int id in ids.Where(id => !s_loop[id]).Take(8))
            {
                double ms = s_frameTicks[id] * s_tickToMs;
                if (ms < 0.5) break;
                sb.Append($" | {(s_nested[id] ? ">" : "")}{Name(id)} {ms:0.0}");
            }
            if (s_loopInstalled)
            {
                sb.Append(" || loop");
                foreach (int id in ids.Where(id => s_loop[id]).Take(6))
                {
                    double ms = s_frameTicks[id] * s_tickToMs;
                    if (ms < 1.0) break;
                    sb.Append($" | {Name(id)} {ms:0.0}");
                }
            }
            var valid = s_counters.Where(c => c.Valid).ToList();
            if (valid.Count > 0)
                sb.Append(" || " + string.Join(" ", valid.Select(c => c.Label + "=" + c.Format(c.Last))));
            Write(sb.ToString());
        }

        static void WriteSummary(long now, bool partial)
        {
            if (s_winFrames == 0) return;
            double avgMs = s_winFrameTicks * s_tickToMs / s_winFrames;
            var sb = new StringBuilder();
            sb.AppendLine($"--- {DateTime.Now:HH:mm:ss} summary{(partial ? " (partial, quitting)" : "")}: {s_winFrames} frames, avg {avgMs:0.0} ms ({1000.0 / avgMs:0} fps), worst {s_winFrameMax * s_tickToMs:0.0} ms, " +
                          $"slow frames {s_winSlow} (hitches >= {HitchMs:0} ms: {s_winHitches}), gen0 GCs {s_winGcs}{CameraPos()}");
            var valid = s_counters.Where(c => c.Valid).ToList();
            if (valid.Count > 0)
                sb.AppendLine("    counters avg/max: " + string.Join(", ", valid.Select(c => $"{c.Label} {c.Format(c.WinSum / s_winFrames)}/{c.Format(c.WinMax)}")));
            foreach (string report in HelperReports())
                sb.AppendLine("    " + report);
            sb.AppendLine("    avg ms/frame |  worst ms | spikes | calls/frame | section");
            var active = Enumerable.Range(0, Cap).Where(id => s_winCalls[id] > 0).OrderByDescending(id => s_winTicks[id]).ToList();
            foreach (int id in active.Where(id => !s_loop[id]).Take(20))
                AppendRow(sb, id, s_nested[id] ? "  >" : "");
            if (s_loopInstalled)
            {
                sb.AppendLine("    -- PlayerLoop (engine + scripts by Unity phase) --");
                foreach (int id in active.Where(id => s_loop[id]).Take(15))
                    AppendRow(sb, id, "  ");
            }
            // The census walks every renderer (5-50 ms): opt-in with an empty "census_on" file next to the log
            if (!partial && File.Exists(Path.Combine(LogDirectory(), "census_on"))) AppendShadowCensus(sb);
            Write(sb.ToString().TrimEnd());
            s_log?.Flush();
        }

        static string CameraPos()
        {
            try
            {
                Camera cam = Camera.main;
                if (cam == null) return "";
                Vector3 p = cam.transform.position;
                return $" @ {p.x:0},{p.z:0}";
            }
            catch
            {
                return "";
            }
        }

        static readonly float[] s_censusBands = { 25f, 50f, 80f };

        /// <summary>
        /// Shadow-casting renderers that Unity drew last frame (camera or shadow map), by layer and camera distance.
        /// Shows which layers fill the sun's shadow maps, to tune Light.layerShadowCullDistances.
        /// </summary>
        static void AppendShadowCensus(StringBuilder sb)
        {
            try
            {
                Camera cam = Camera.main;
                if (cam == null) return;
                long start = Stopwatch.GetTimestamp();
                Vector3 camPos = cam.transform.position;
                Renderer[] renderers = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None);
                int bands = s_censusBands.Length + 1;
                var counts = new int[32 * bands];
                int casters = 0;
                foreach (Renderer r in renderers)
                {
                    if (!r.enabled || r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off || !r.isVisible) continue;
                    float d = Vector3.Distance(camPos, r.bounds.center);
                    int band = 0;
                    while (band < s_censusBands.Length && d >= s_censusBands[band]) band++;
                    counts[r.gameObject.layer * bands + band]++;
                    casters++;
                }
                double ms = (Stopwatch.GetTimestamp() - start) * s_tickToMs;
                string header = string.Join("/", new[] { "<" + s_censusBands[0] }.Concat(Enumerable.Range(1, s_censusBands.Length - 1).Select(i => $"{s_censusBands[i - 1]}-{s_censusBands[i]}")).Concat(new[] { s_censusBands[s_censusBands.Length - 1] + "+" }));
                sb.AppendLine($"    -- shadow casters drawn, by layer ({header} m from camera; {casters} of {renderers.Length} renderers, shadowDistance {QualitySettings.shadowDistance:0} m, census {ms:0.0} ms) --");
                var layers = Enumerable.Range(0, 32).Select(l => (Layer: l, Total: Enumerable.Range(0, bands).Sum(b => counts[l * bands + b])))
                    .Where(x => x.Total > 0).OrderByDescending(x => x.Total).Take(10);
                foreach (var (layer, total) in layers)
                {
                    string name = LayerMask.LayerToName(layer);
                    string split = string.Join("/", Enumerable.Range(0, bands).Select(b => counts[layer * bands + b]));
                    sb.AppendLine($"    {total,12} | {split,-21} | {layer,2} {(string.IsNullOrEmpty(name) ? "?" : name)}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("    shadow census failed: " + ex.Message);
            }
        }

        // NativePatchMist.dll (Mistlands fog rewrite) is a separate helper; looked up by name so the profiler works without it
        static System.Reflection.MethodInfo[] s_helperReports;

        // Statistics lines of the patch helper (mist emitter, zone unloading), when it is installed
        static IEnumerable<string> HelperReports()
        {
            if (s_helperReports == null)
                s_helperReports = new[] { "NativePatchMist.MistTick", "NativePatchMist.ZoneTtl", "NativePatchMist.SceneObjects" }
                    .Select(t => Type.GetType(t + ", NativePatchMist")?.GetMethod("TakeReport")).Where(m => m != null).ToArray();
            foreach (var m in s_helperReports)
            {
                string line;
                try { line = (string)m.Invoke(null, null); }
                catch { line = null; }
                if (line != null) yield return line;
            }
        }

        static void AppendRow(StringBuilder sb, int id, string prefix)
        {
            sb.AppendLine($"    {s_winTicks[id] * s_tickToMs / s_winFrames,12:0.000} | {s_winMaxFrame[id] * s_tickToMs,9:0.0} | {s_winSpikes[id],6} | {(double)s_winCalls[id] / s_winFrames,11:0.0} | {prefix}{Name(id)}");
        }

        static string Name(int id) => s_names[id] ?? ("section#" + id);

        static void ResetFrame()
        {
            for (int i = 0; i < s_touchedCount; i++)
            {
                int id = s_touchedList[i];
                s_frameTicks[id] = 0;
                s_touched[id] = false;
            }
            s_touchedCount = 0;
        }

        static void ResetWindow(long now)
        {
            Array.Clear(s_winTicks, 0, Cap);
            Array.Clear(s_winMaxFrame, 0, Cap);
            Array.Clear(s_winCalls, 0, Cap);
            Array.Clear(s_winSpikes, 0, Cap);
            s_winFrames = s_winFrameTicks = s_winFrameMax = 0;
            s_winSlow = s_winHitches = s_winGcs = s_winSlowLogged = s_winHitchLogged = 0;
            s_winStart = now;
            foreach (var c in s_counters) c.WinSum = c.WinMax = 0;
        }

        static void Write(string line)
        {
            try
            {
                if (s_log == null)
                {
                    string dir = LogDirectory();
                    Directory.CreateDirectory(dir);
                    s_log = new StreamWriter(Path.Combine(dir, "native_profile.log"), append: true, new UTF8Encoding(false));
                }
                s_log.WriteLine(line);
            }
            catch
            {
                // No log, no crash
            }
        }

        static string LogDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string macLogs = Path.Combine(home, "Library", "Logs");
            if (Directory.Exists(macLogs))
                return Path.Combine(macLogs, "IronGate", "Valheim");
            return Path.Combine(home, "AppData", "LocalLow", "IronGate", "Valheim");
        }
    }
}
