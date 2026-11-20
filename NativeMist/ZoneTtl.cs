using System;
using System.Collections.Generic;
using UnityEngine;

namespace NativePatchMist
{
    /// <summary>
    /// Replaces ZNetScene.HaveInstanceInSector, whose only caller is ZoneSystem.UpdateTTL (every 0.1 s, once per expired
    /// zone until one without objects is unloaded). Vanilla walks every instance and asks the engine for its position, per
    /// expired zone: zones x instances native calls per tick, which kept growing over a session (up to 50 ms every 6th
    /// frame) until a restart. Same answer here: a sector is occupied iff some live, non-distant instance stands in it.
    /// Each sector remembers the instance that proved it occupied (re-checked in O(1) next tick); otherwise one scan per
    /// frame is resumed where the previous query of that frame stopped, recording a witness for every sector it passes.
    /// </summary>
    public static class ZoneTtl
    {
        const int MaxWitnesses = 1024;

        static readonly Dictionary<Vector2s, ZNetView> s_witness = new Dictionary<Vector2s, ZNetView>();
        static Dictionary<ZDO, ZNetView> s_scanSource;
        static Dictionary<ZDO, ZNetView>.Enumerator s_scan;
        static int s_scanFrame = -1;
        static bool s_scanDone;

        // Statistics (reset by TakeReport)
        static long s_queries, s_hits, s_scanned, s_empty, s_frames;
        static int s_instancesMax, s_lastFrame = -1;

        public static bool HaveInstanceInSector(Dictionary<ZDO, ZNetView> instances, Vector2s sector)
        {
            s_queries++;
            int frame = Time.frameCount;
            if (frame != s_lastFrame) { s_lastFrame = frame; s_frames++; }
            if (instances.Count > s_instancesMax) s_instancesMax = instances.Count;

            if (s_witness.TryGetValue(sector, out ZNetView w))
            {
                if (Occupies(instances, w, sector)) { s_hits++; return true; }
                s_witness.Remove(sector);
            }

            if (frame != s_scanFrame || !ReferenceEquals(instances, s_scanSource))
            {
                if (s_witness.Count > MaxWitnesses) s_witness.Clear();
                s_scanSource = instances;
                s_scan = instances.GetEnumerator();
                s_scanFrame = frame;
                s_scanDone = false;
            }
            if (!s_scanDone)
            {
                try
                {
                    while (s_scan.MoveNext())
                    {
                        s_scanned++;
                        ZNetView nv = s_scan.Current.Value;
                        if (!nv || nv.m_distant) continue;
                        Vector2s s = ZoneSystem.GetZone(nv.transform.position);
                        s_witness[s] = nv;
                        if (s == sector) return true;
                    }
                    s_scanDone = true;
                }
                catch (InvalidOperationException)
                {
                    // The dictionary changed under the resumed scan: answer like vanilla and start afresh next time
                    s_scanFrame = -1;
                    return VanillaScan(instances, sector);
                }
            }
            s_empty++;
            return false;
        }

        // The witness must still be one of the scene's instances, alive, non-distant and inside the sector
        static bool Occupies(Dictionary<ZDO, ZNetView> instances, ZNetView nv, Vector2s sector)
        {
            if (!nv || nv.m_distant) return false;
            ZDO zdo = nv.GetZDO();
            if (zdo == null || !instances.TryGetValue(zdo, out ZNetView current) || !ReferenceEquals(current, nv)) return false;
            return ZoneSystem.GetZone(nv.transform.position) == sector;
        }

        static bool VanillaScan(Dictionary<ZDO, ZNetView> instances, Vector2s sector)
        {
            foreach (KeyValuePair<ZDO, ZNetView> instance in instances)
            {
                s_scanned++;
                if ((bool)instance.Value && !instance.Value.m_distant && ZoneSystem.GetZone(instance.Value.transform.position) == sector)
                    return true;
            }
            s_empty++;
            return false;
        }

        /// <summary>One summary line for the profiler; resets the counters.</summary>
        public static string TakeReport()
        {
            if (s_queries == 0) return null;
            long ticks = Math.Max(1, s_frames);
            string line = $"zones: ttl ticks {s_frames}, expired-zone checks {(double)s_queries / ticks:0.0}/tick " +
                          $"({100.0 * s_hits / s_queries:0}% by remembered object), instances scanned {(double)s_scanned / ticks:0}/tick, " +
                          $"empty -> unloaded {s_empty}, scene instances max {s_instancesMax}";
            s_queries = s_hits = s_scanned = s_empty = s_frames = 0;
            s_instancesMax = 0;
            return line;
        }
    }
}
