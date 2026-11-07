using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace NativePatchMist
{
    /// <summary>
    /// Replacement for the body of ParticleMist.Update (Mistlands fog emitter), injected by the patcher.
    ///
    /// Same emission rules, rates and distributions as vanilla, but:
    ///  - mister / demister positions and radii are snapshotted once per 0.1 s tick, so the per-candidate "inside which
    ///    mister / demister" tests no longer make native Transform.position calls (the bulk of the vanilla cost);
    ///  - a tick's emission is spread over the next few frames instead of one 1-3 ms burst every 5th frame;
    ///  - particles that would spawn entirely inside the terrain are not emitted (they are hidden by the depth test anyway
    ///    but still cost a particle slot, simulation and vertex work);
    ///  - live particles are capped below the system's maxParticles (distant/demister mist lower than the mist around the
    ///    player, so the near fog is thinned last); the fractions can be tuned live with a "mist_cap" file ("0.45 0.65");
    ///  - per-minute statistics are exposed to the profiler (TakeReport), and an empty file "mist_off" next to the logs
    ///    switches emission off for A/B timing in the same spot.
    /// </summary>
    public static class MistTick
    {
        const float TickSeconds = 0.1f;          // vanilla cadence: Time.fixedDeltaTime accumulated per rendered frame
        const int SpreadFrames = 4;              // a tick is 5 frames long; emit during the first 4
        const float DefaultFarCap = 0.45f;       // distant mist and demister mist: share of maxParticles
        const float DefaultNearCap = 0.65f;      // mist around the player
        const float GroundMarginScale = 0.75f;   // × startSize: a quad centred this deep below the surface is fully buried
        const float GroundMarginFlat = 1f;
        const float DriftMargin = 8f;            // distant mist drifts sideways, so it needs extra depth before it counts as buried

        struct M { public float X, Y, Z, R, Top; }
        struct D { public Demister Ref; public float X, Y, Z, Range; }

        enum Kind : byte { Local, Field, Mister }

        struct Job
        {
            public Kind Kind;
            public int Index;            // field index (ignored by the demister test) or mister index
            public float X, Y, Z;        // centre
            public float Radius, Thickness, Top;
            public int Remaining;
        }

        // Per-instance state (ParticleMist is a scene singleton; reset when the instance changes)
        static ParticleMist s_self;
        static ParticleSystem s_ps;
        static Transform s_tr;
        static float s_acc;
        static float s_combined;
        static Vector3 s_last;

        // Per-tick snapshot
        static M[] s_m = new M[64];
        static int s_mCount;
        static D[] s_d = new D[16];
        static int s_dCount;
        static Job[] s_jobs = new Job[64];
        static int s_jobCount, s_jobPos, s_pending, s_framesLeft;
        static Vector3 s_psPos;
        static float s_minAlt;
        static int s_live, s_max, s_cap, s_capNear;
        static float s_farFrac = DefaultFarCap, s_nearFrac = DefaultNearCap;

        // vanilla builds a fresh EmitParams per call; only the fields it sets may be overridden (velocity must stay untouched for near mist)
        static ParticleSystem.EmitParams s_epNear;
        static ParticleSystem.EmitParams s_epFar;

        // A/B switch
        static float s_nextFileCheck;
        static bool s_off;

        // Statistics (reset by TakeReport)
        static long s_ticks, s_frames, s_workTicks, s_worstFrameTicks, s_attempts, s_spawned, s_skipGround, s_skipCap, s_liveSum, s_liveMax;
        static int s_lastMax, s_lastCap, s_lastCapNear;

        /// <summary>Called instead of ParticleMist.Update; returns the new value of ParticleMist.m_haveActiveMist.</summary>
        public static bool Tick(MonoBehaviour behaviour, bool haveActive)
        {
            var self = (ParticleMist)behaviour;
            if (!ReferenceEquals(self, s_self)) Attach(self);

            if (!s_ps.emission.enabled)
            {
                s_jobCount = s_pending = 0;
                return haveActive;
            }
            if (CheckOffSwitch()) return haveActive;

            s_acc += Time.fixedDeltaTime;
            bool tick = s_acc >= TickSeconds;
            if (!tick && s_pending == 0) return haveActive;

            long t0 = Stopwatch.GetTimestamp();
            if (tick)
            {
                s_acc -= TickSeconds;
                BeginTick(self, ref haveActive);
            }
            if (s_pending > 0) Drain();
            long dt = Stopwatch.GetTimestamp() - t0;
            s_frames++;
            s_workTicks += dt;
            if (dt > s_worstFrameTicks) s_worstFrameTicks = dt;
            return haveActive;
        }

        static void Attach(ParticleMist self)
        {
            s_self = self;
            s_ps = self.GetComponent<ParticleSystem>();
            s_tr = self.transform;
            s_last = s_tr.position;
            s_acc = s_combined = 0f;
            s_jobCount = s_pending = 0;
            s_epNear = default;
            s_epFar = default;
        }

        // ---------- tick: snapshot + plan ----------

        static void BeginTick(ParticleMist pm, ref bool haveActive)
        {
            s_jobCount = s_jobPos = s_pending = 0;
            Player player = Player.m_localPlayer;
            if (player == null) return;
            var misters = Mister.GetDemistersSorted(player.transform.position);   // keeps vanilla's side effect on the shared list order
            if (misters.Count == 0) return;
            haveActive = true;
            if (ZoneSystem.instance == null) return;

            s_psPos = s_tr.position;
            SnapshotMisters(misters);
            SnapshotDemisters();

            float moved = Vector3.Distance(s_psPos, s_last);
            s_combined += Mathf.Clamp(moved, 0f, 10f);
            s_last = s_psPos;
            s_minAlt = FindMinMistAltitude(50f);

            int num = (int)(s_combined * pm.m_localEmissionPerUnit);
            if (num > 0) s_combined = Mathf.Max(0f, s_combined - num / (float)pm.m_localEmissionPerUnit);
            int toEmit = (int)(pm.m_localEmission * 0.1f) + num;
            AddEmitJob(Kind.Local, -1, s_psPos.x, s_psPos.y, s_psPos.z, 0f, pm.m_localRange, toEmit);

            for (int i = 0; i < s_dCount; i++)
            {
                ref D d = ref s_d[i];
                float num2 = Mathf.Max(0f, Dist(d.X, d.Y, d.Z, s_psPos.x, s_psPos.y, s_psPos.z) - d.Range);
                if (num2 > pm.m_maxDistance) continue;
                float area = MathF.PI * 4f * (d.Range * d.Range);
                float rate = Mathf.Lerp(pm.m_emissionMax, 0f, Utils.LerpStep(pm.m_minDistance, pm.m_maxDistance, num2));
                int n = (int)(area * rate * 0.1f);
                float movedDistance = d.Ref.GetMovedDistance();
                n += (int)(movedDistance * pm.m_emissionPerUnit);
                AddEmitJob(Kind.Field, i, d.X, d.Y, d.Z, d.Range, 0f, n);
            }

            for (int i = 0; i < s_mCount; i++)
            {
                ref M m = ref s_m[i];
                float num2 = Mathf.Max(0f, Dist(m.X, m.Y, m.Z, s_psPos.x, s_psPos.y, s_psPos.z) - m.R);
                if (num2 > pm.m_distantMaxRange || IsCompletelyInsideOtherMister(i, pm.m_distantThickness)) continue;
                float area = MathF.PI * 4f * (m.R * m.R);
                float rate = Mathf.Lerp(pm.m_distantEmissionMax, 0f, Utils.LerpStep(0f, pm.m_distantMaxRange, num2));
                int n = (int)(area * rate * 0.1f);
                if (n <= 0) continue;
                Reserve();
                s_jobs[s_jobCount++] = new Job { Kind = Kind.Mister, Index = i, X = m.X, Y = m.Y, Z = m.Z, Radius = m.R, Top = m.Top, Remaining = n };
                s_pending += n;
            }

            s_framesLeft = SpreadFrames;
            s_live = s_ps.particleCount;
            s_max = s_ps.main.maxParticles;
            s_cap = (int)(s_max * s_farFrac);
            s_capNear = (int)(s_max * s_nearFrac);
            s_ticks++;
            s_liveSum += s_live;
            if (s_live > s_liveMax) s_liveMax = s_live;
            s_lastMax = s_max;
            s_lastCap = s_cap;
            s_lastCapNear = s_capNear;
        }

        // vanilla ParticleMist.Emit's early-out, evaluated once per job
        static void AddEmitJob(Kind kind, int index, float x, float y, float z, float radius, float thickness, int count)
        {
            if (count <= 0) return;
            float reach = radius + thickness;
            if (!InsideMister(x, y, z, reach) || IsInsideOtherDemister(x, y, z, reach, kind == Kind.Field ? index : -1)) return;
            Reserve();
            s_jobs[s_jobCount++] = new Job { Kind = kind, Index = index, X = x, Y = y, Z = z, Radius = radius, Thickness = thickness, Remaining = count };
            s_pending += count;
        }

        static void Reserve()
        {
            if (s_jobCount == s_jobs.Length) Array.Resize(ref s_jobs, s_jobs.Length * 2);
        }

        static void SnapshotMisters(System.Collections.Generic.List<Mister> list)
        {
            if (s_m.Length < list.Count) s_m = new M[Math.Max(list.Count, s_m.Length * 2)];
            s_mCount = 0;
            foreach (Mister m in list)
            {
                Vector3 p = m.transform.position;
                s_m[s_mCount++] = new M { X = p.x, Y = p.y, Z = p.z, R = m.m_radius, Top = p.y + m.m_height };
            }
        }

        static void SnapshotDemisters()
        {
            var list = Demister.GetDemisters();
            if (s_d.Length < list.Count) s_d = new D[Math.Max(list.Count, s_d.Length * 2)];
            s_dCount = 0;
            foreach (Demister d in list)
            {
                Vector3 p = d.transform.position;
                s_d[s_dCount++] = new D { Ref = d, X = p.x, Y = p.y, Z = p.z, Range = d.m_forceField != null ? d.m_forceField.endRange : 0f };
            }
        }

        // vanilla FindMaxMistAlltitude, minimum only
        static float FindMinMistAltitude(float testRange)
        {
            float min = 99999f;
            for (int i = 0; i < 20; i++)
            {
                Vector2 c = UnityEngine.Random.insideUnitCircle;
                float h = ZoneSystem.instance.GetGroundHeight(new Vector3(s_psPos.x + c.x * testRange, 0f, s_psPos.z + c.y * testRange));
                if (h < min) min = h;
            }
            return min;
        }

        // ---------- per-frame emission ----------

        static void Drain()
        {
            int frames = Math.Max(1, s_framesLeft);
            if (s_framesLeft > 0) s_framesLeft--;
            int budget = (s_pending + frames - 1) / frames;
            while (budget > 0 && s_jobPos < s_jobCount)
            {
                ref Job j = ref s_jobs[s_jobPos];
                int run = Math.Min(budget, j.Remaining);
                for (int k = 0; k < run; k++)
                {
                    if (j.Kind == Kind.Mister) AttemptFar(ref j); else AttemptNear(ref j);
                }
                s_attempts += run;
                j.Remaining -= run;
                budget -= run;
                s_pending -= run;
                if (j.Remaining <= 0) s_jobPos++;
            }
        }

        // vanilla ParticleMist.Emit loop body (local mist around the player and mist around demisters)
        static void AttemptNear(ref Job j)
        {
            var pm = s_self;
            Vector3 dir = UnityEngine.Random.onUnitSphere;
            float reach = j.Radius + 0.1f + UnityEngine.Random.Range(0f, j.Thickness);
            float x = j.X + dir.x * reach, y = j.Y + dir.y * reach, z = j.Z + dir.z * reach;
            if (y < s_minAlt) return;
            if (IsInsideOtherDemister(x, y, z, 0f, j.Kind == Kind.Field ? j.Index : -1)) return;
            if (!InsideMister(x, y, z, 0f)) return;
            float d = Dist(s_psPos.x, s_psPos.y, s_psPos.z, x, y, z);
            if (d > pm.m_maxDistance) return;
            float size = Mathf.Lerp(pm.m_minSize, pm.m_maxSize, Utils.LerpStep(pm.m_minDistance, pm.m_maxDistance, d));
            if (!Spawnable(x, y, z, size, j.Kind == Kind.Local ? s_capNear : s_cap, false)) return;
            s_epNear.startSize = size;
            s_epNear.position = new Vector3(x, y, z);
            s_ps.Emit(s_epNear, 1);
            s_live++;
            s_spawned++;
        }

        // vanilla ParticleMist.MisterEmit loop body (distant mist walls)
        static void AttemptFar(ref Job j)
        {
            var pm = s_self;
            Vector3 dir = UnityEngine.Random.onUnitSphere;
            float reach = j.Radius + 0.1f + UnityEngine.Random.Range(0f, pm.m_distantThickness);
            float x = j.X + dir.x * reach, y = j.Y + dir.y * reach, z = j.Z + dir.z * reach;
            if (y < s_minAlt) return;
            if (y > j.Top) y = j.Top;
            if (IsInsideOtherMister(x, y, z, j.Index) || IsInsideOtherDemister(x, y, z, 0f, -1)) return;
            float d = Dist(s_psPos.x, s_psPos.y, s_psPos.z, x, y, z);
            if (d > pm.m_distantMaxRange) return;
            float size = Mathf.Lerp(pm.m_distantMinSize, pm.m_distantMaxSize, Utils.LerpStep(0f, pm.m_distantMaxRange, d));
            Vector3 velocity = dir * UnityEngine.Random.Range(0f, pm.m_distantEmissionMaxVel);
            velocity.y = 0f;
            if (!Spawnable(x, y, z, size, s_cap, true)) return;
            s_epFar.startSize = size;
            s_epFar.position = new Vector3(x, y, z);
            s_epFar.velocity = velocity;
            s_ps.Emit(s_epFar, 1);
            s_live++;
            s_spawned++;
        }

        static bool Spawnable(float x, float y, float z, float size, int cap, bool drifting)
        {
            if (s_live >= cap)
            {
                s_skipCap++;
                return false;
            }
            if (ZoneSystem.instance.GetGroundHeight(new Vector3(x, y, z), out float ground))
            {
                float margin = size * GroundMarginScale + GroundMarginFlat + (drifting ? DriftMargin : 0f);
                if (ground - y > margin)
                {
                    s_skipGround++;
                    return false;
                }
            }
            return true;
        }

        // ---------- vanilla Mister / Demister queries on the snapshot ----------

        static float Dist(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = ax - bx, dy = ay - by, dz = az - bz;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        // Mister.InsideMister(p, radius)
        static bool InsideMister(float x, float y, float z, float radius)
        {
            for (int i = 0; i < s_mCount; i++)
            {
                ref M m = ref s_m[i];
                float dx = m.X - x, dy = m.Y - y, dz = m.Z - z;
                float r = m.R + radius;
                if (dx * dx + dy * dy + dz * dz < r * r && y - radius < m.Top) return true;
            }
            return false;
        }

        // Mister.IsInsideOtherMister(p, ignore)
        static bool IsInsideOtherMister(float x, float y, float z, int ignore)
        {
            for (int i = 0; i < s_mCount; i++)
            {
                if (i == ignore) continue;
                ref M m = ref s_m[i];
                float dx = m.X - x, dy = m.Y - y, dz = m.Z - z;
                if (dx * dx + dy * dy + dz * dz < m.R * m.R && y < m.Top) return true;
            }
            return false;
        }

        // Mister.IsCompletelyInsideOtherMister(thickness)
        static bool IsCompletelyInsideOtherMister(int self, float thickness)
        {
            ref M a = ref s_m[self];
            for (int i = 0; i < s_mCount; i++)
            {
                if (i == self) continue;
                ref M b = ref s_m[i];
                if (Dist(a.X, a.Y, a.Z, b.X, b.Y, b.Z) + a.R + thickness < b.R && a.Top < b.Top) return true;
            }
            return false;
        }

        // ParticleMist.IsInsideOtherDemister(fields, p, radius, ignore)
        static bool IsInsideOtherDemister(float x, float y, float z, float radius, int ignore)
        {
            for (int i = 0; i < s_dCount; i++)
            {
                if (i == ignore) continue;
                ref D d = ref s_d[i];
                if (Dist(d.X, d.Y, d.Z, x, y, z) + radius < d.Range) return true;
            }
            return false;
        }

        // ---------- A/B switch & statistics ----------

        static bool CheckOffSwitch()
        {
            float now = Time.realtimeSinceStartup;
            if (now >= s_nextFileCheck)
            {
                s_nextFileCheck = now + 2f;
                bool off;
                try { off = File.Exists(Path.Combine(LogDirectory(), "mist_off")); }
                catch { off = false; }
                ReadCapFile();
                if (off && !s_off)
                {
                    s_ps.Clear();
                    s_jobCount = s_pending = 0;
                }
                s_off = off;
            }
            return s_off;
        }

        // "mist_cap" next to the logs: "<far> <near>" fractions of maxParticles, e.g. "0.45 0.65"; removing the file restores the defaults
        static void ReadCapFile()
        {
            float far = DefaultFarCap, near = DefaultNearCap;
            try
            {
                string path = Path.Combine(LogDirectory(), "mist_cap");
                if (File.Exists(path))
                {
                    string[] parts = File.ReadAllText(path).Split(new[] { ' ', '\t', '\r', '\n', ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (parts.Length > 0 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out float f)) far = Mathf.Clamp(f, 0.05f, 1f);
                    near = far;
                    if (parts.Length > 1 && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out float n)) near = Mathf.Clamp(n, 0.05f, 1f);
                }
            }
            catch { }
            s_farFrac = far;
            s_nearFrac = near;
        }

        static string LogDirectory()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string macLogs = Path.Combine(home, "Library", "Logs");
            if (Directory.Exists(macLogs))
                return Path.Combine(macLogs, "IronGate", "Valheim");
            return Path.Combine(home, "AppData", "LocalLow", "IronGate", "Valheim");
        }

        /// <summary>One line for the profiler's per-minute summary; null when nothing was emitted. Resets the counters.</summary>
        public static string TakeReport()
        {
            if (s_ticks == 0 && !s_off) return null;
            double toMs = 1000.0 / Stopwatch.Frequency;
            double avgWork = s_frames > 0 ? s_workTicks * toMs / s_frames : 0;
            string line = s_off
                ? "mist: EMISSION OFF (mist_off file present)"
                : $"mist: ticks {s_ticks}, work {avgWork:0.000} ms/frame (worst {s_worstFrameTicks * toMs:0.00}) over {s_frames} frames, " +
                  $"live particles avg {(s_ticks > 0 ? s_liveSum / s_ticks : 0)}/max {s_liveMax} (maxParticles {s_lastMax}, caps far {s_lastCap}/near {s_lastCapNear}), " +
                  $"attempts {s_attempts} -> spawned {s_spawned}, skipped underground {s_skipGround}, over cap {s_skipCap}";
            s_ticks = s_frames = s_workTicks = s_worstFrameTicks = s_attempts = s_spawned = s_skipGround = s_skipCap = s_liveSum = s_liveMax = 0;
            return line;
        }
    }
}
