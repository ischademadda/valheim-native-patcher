using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace NativePatchProfiler
{
    /// <summary>
    /// Lightweight main-thread section timer injected into assembly_valheim.dll by the patcher (--profile).
    /// Writes slow-frame breakdowns and a per-minute summary to native_profile.log next to Player.log.
    /// </summary>
    public static class Prof
    {
        const int Cap = 4096;
        const double SlowFrameMs = 33.0;   // below 30 fps for a single frame = visible hitch
        const double SpikeMs = 4.0;        // a single section eating 4 ms is worth counting
        const double WindowSeconds = 60.0;
        const int MaxSlowLinesPerWindow = 40;

        static readonly double s_tickToMs = 1000.0 / Stopwatch.Frequency;

        static readonly string[] s_names = new string[Cap];
        static readonly bool[] s_nested = new bool[Cap];
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
        static int s_winSlow, s_winGcs, s_winSlowLogged;

        static long s_lastFrame;
        static int s_lastGcCount;
        static StreamWriter s_log;

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
            if (!s_scopeIds.TryGetValue(scope, out int id))
            {
                if (s_nextScopeId <= 0) return;
                id = s_nextScopeId--;
                s_scopeIds[scope] = id;
                s_names[id] = scope;
                s_nested[id] = true; // always runs inside MonoUpdaters.Update/FixedUpdate/LateUpdate
            }
            Add(id, dt);
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

        /// <summary>Called once per frame at the start of Game.Update.</summary>
        public static void Frame()
        {
            try
            {
                FrameCore();
            }
            catch (Exception ex)
            {
                // Never let the profiler break the game
                Write("profiler error: " + ex.Message);
            }
        }

        static void FrameCore()
        {
            long now = Stopwatch.GetTimestamp();
            if (s_lastFrame == 0)
            {
                ResetFrame();
                ResetWindow(now);
                s_lastFrame = now;
                s_lastGcCount = GC.CollectionCount(0);
                Write($"=== Valheim native profiler started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (slow frame >= {SlowFrameMs} ms, spike >= {SpikeMs} ms)");
                return;
            }

            long frameTicks = now - s_lastFrame;
            s_lastFrame = now;
            s_winFrames++;
            s_winFrameTicks += frameTicks;
            if (frameTicks > s_winFrameMax) s_winFrameMax = frameTicks;

            int gcCount = GC.CollectionCount(0);
            bool gcThisFrame = gcCount != s_lastGcCount;
            s_lastGcCount = gcCount;
            if (gcThisFrame) s_winGcs++;

            double frameMs = frameTicks * s_tickToMs;
            if (frameMs >= SlowFrameMs)
            {
                s_winSlow++;
                if (s_winSlowLogged < MaxSlowLinesPerWindow)
                {
                    s_winSlowLogged++;
                    LogSlowFrame(frameMs, frameTicks, gcThisFrame);
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
                WriteSummary(now);
                ResetWindow(now);
            }
        }

        static void LogSlowFrame(double frameMs, long frameTicks, bool gc)
        {
            long topLevel = 0;
            for (int i = 0; i < s_touchedCount; i++)
            {
                int id = s_touchedList[i];
                if (!s_nested[id]) topLevel += s_frameTicks[id];
            }
            var top = Enumerable.Range(0, s_touchedCount)
                .Select(i => s_touchedList[i])
                .OrderByDescending(id => s_frameTicks[id])
                .Take(8);
            var sb = new StringBuilder();
            sb.Append($"{DateTime.Now:HH:mm:ss} SLOW {frameMs,6:0.0} ms{(gc ? " [GC]" : "")} | scripts {topLevel * s_tickToMs:0.0} | engine/render/wait {(frameTicks - topLevel) * s_tickToMs:0.0}");
            foreach (int id in top)
            {
                double ms = s_frameTicks[id] * s_tickToMs;
                if (ms < 0.5) break;
                sb.Append($" | {(s_nested[id] ? ">" : "")}{Name(id)} {ms:0.0}");
            }
            Write(sb.ToString());
        }

        static void WriteSummary(long now)
        {
            if (s_winFrames == 0) return;
            double avgMs = s_winFrameTicks * s_tickToMs / s_winFrames;
            var sb = new StringBuilder();
            sb.AppendLine($"--- {DateTime.Now:HH:mm:ss} summary: {s_winFrames} frames, avg {avgMs:0.0} ms ({1000.0 / avgMs:0} fps), worst {s_winFrameMax * s_tickToMs:0.0} ms, slow frames {s_winSlow}, gen0 GCs {s_winGcs}");
            sb.AppendLine("    avg ms/frame |  worst ms | spikes | calls/frame | section");
            var top = Enumerable.Range(0, Cap)
                .Where(id => s_winCalls[id] > 0)
                .OrderByDescending(id => s_winTicks[id])
                .Take(20);
            foreach (int id in top)
            {
                sb.AppendLine($"    {s_winTicks[id] * s_tickToMs / s_winFrames,12:0.000} | {s_winMaxFrame[id] * s_tickToMs,9:0.0} | {s_winSpikes[id],6} | {(double)s_winCalls[id] / s_winFrames,11:0.0} | {(s_nested[id] ? "  >" : "")}{Name(id)}");
            }
            Write(sb.ToString().TrimEnd());
            s_log?.Flush();
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
            s_winSlow = s_winGcs = s_winSlowLogged = 0;
            s_winStart = now;
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
