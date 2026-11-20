using System.Collections.Generic;
using UnityEngine;

namespace NativePatchMist
{
    /// <summary>
    /// ZNetScene.RemoveObjects and ZNetScene.OnZDODestroyed, tolerant of instances whose GameObject was destroyed by other
    /// game code (e.g. dungeon generation after a teleport). ZNetView.OnDestroy does not unregister the view, so vanilla
    /// later calls .gameObject on the dead view and throws, after ResetZDO and before m_instances.Remove; the entry stays
    /// and RemoveObjects throws on it every 1/30 s for the rest of the session. From then on nothing is ever unloaded
    /// (22k live instances, zones never released, fps falling until a restart). Live entries are handled exactly as vanilla.
    /// </summary>
    public static class SceneObjects
    {
        const int MaxLogged = 20;

        static readonly List<KeyValuePair<ZDO, ZNetView>> s_removed = new List<KeyValuePair<ZDO, ZNetView>>();
        static int s_logged;
        static long s_dropped;

        public static void RemoveObjects(Dictionary<ZDO, ZNetView> instances, List<ZDO> currentNearObjects, List<ZDO> currentDistantObjects)
        {
            byte b = (byte)(Time.frameCount & 0xFF);
            foreach (ZDO zdo in currentNearObjects) zdo.TempRemoveEarmark = b;
            foreach (ZDO zdo in currentDistantObjects) zdo.TempRemoveEarmark = b;

            s_removed.Clear();
            foreach (KeyValuePair<ZDO, ZNetView> instance in instances)
            {
                ZDO zdo = (object)instance.Value != null ? instance.Value.GetZDO() : null;
                if (zdo == null || zdo.TempRemoveEarmark != b) s_removed.Add(instance);
            }
            for (int i = 0; i < s_removed.Count; i++)
            {
                ZNetView view = s_removed[i].Value;
                ZDO zdo = (object)view != null ? view.GetZDO() : null;
                if (zdo == null)
                {
                    // Already reset by an earlier failed removal: its ZDO may be destroyed or pooled, only drop the entry
                    Drop(instances, s_removed[i].Key, view);
                    continue;
                }
                view.ResetZDO();
                if (view) Object.Destroy(view.gameObject);
                else Dropped(zdo);
                if (!zdo.Persistent && zdo.IsOwner()) ZDOMan.instance.DestroyZDO(zdo);
                instances.Remove(zdo);
            }
            s_removed.Clear();
        }

        public static void OnZDODestroyed(Dictionary<ZDO, ZNetView> instances, ZDO zdo)
        {
            if (!instances.TryGetValue(zdo, out ZNetView view)) return;
            if ((object)view != null && view.GetZDO() != null) view.ResetZDO();
            if (view) Object.Destroy(view.gameObject);
            else Dropped(zdo);
            instances.Remove(zdo);
        }

        static void Drop(Dictionary<ZDO, ZNetView> instances, ZDO key, ZNetView view)
        {
            if (view) Object.Destroy(view.gameObject);
            Dropped(key);
            instances.Remove(key);
        }

        static void Dropped(ZDO zdo)
        {
            s_dropped++;
            if (s_logged >= MaxLogged) return;
            s_logged++;
            string name = "?";
            try { name = ZNetScene.instance?.GetPrefab(zdo.GetPrefab())?.name ?? zdo.GetPrefab().ToString(); }
            catch { }
            Debug.Log($"[NativePatch] ZNetScene: unregistered an instance destroyed outside ZNetScene ({name} @ {zdo.GetPosition()}); vanilla RemoveObjects would throw on it from now on");
        }

        /// <summary>Count of dead instances cleaned up since the last report (null when none).</summary>
        public static string TakeReport()
        {
            if (s_dropped == 0) return null;
            string line = $"scene: {s_dropped} instance(s) destroyed outside ZNetScene unregistered (vanilla would stop unloading objects)";
            s_dropped = 0;
            return line;
        }
    }
}
