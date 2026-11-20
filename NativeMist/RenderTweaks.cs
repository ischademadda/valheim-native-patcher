using UnityEngine;
using UnityEngine.Rendering;
using UnityStandardAssets.ImageEffects;

namespace NativePatchMist
{
    /// <summary>
    /// Image-identical rendering shortcuts, called from patched EnvMan.SetEnv and ReflectionUpdate.Start.
    /// (Lives in the mist helper so the game only loads one extra assembly.)
    /// </summary>
    public static class RenderTweaks
    {
        // The shaft mask is saturate(maxRadius - |sunPos - uv|) in viewport units and the radial blur only gathers on-screen
        // texels, so when no screen point is within maxRadius of the sun the effect adds exactly zero. The margin covers a
        // camera that turns between the pre-cull test and the post-effect.
        const float ShaftsMargin = 0.25f;

        // The environment reflection probe (128 px cube, every 3 s) renders pieces, terrain and statics with sun shadows up
        // to 150 m for each of its six faces. Standing still, re-render every 12 s instead; moving, keep vanilla's interval.
        const float ProbeMoveMeters = 4f;
        const float ProbeIdleInterval = 12f;

        static SunShafts s_shafts;
        static Camera s_cam;
        static bool s_hooked;
        static ReflectionUpdate s_reflection;
        static float s_vanillaInterval;

        /// <summary>EnvMan.SetEnv, right after it sets SunShafts.sunColor: remember the effect and its camera.</summary>
        public static void TrackSunShafts(MonoBehaviour shafts, Camera cam)
        {
            s_shafts = (SunShafts)shafts;
            s_cam = cam;
            if (s_hooked) return;
            s_hooked = true;
            Camera.onPreCull += OnPreCull;
            Debug.Log("[NativePatch] SunShafts skipped while the sun is off screen (image-identical)");
        }

        // Runs after every LateUpdate with the camera at its final pose for this frame
        static void OnPreCull(Camera cam)
        {
            if (!ReferenceEquals(cam, s_cam)) return;
            if (s_reflection != null) AdaptProbeInterval();
            if (s_shafts == null) return;
            var settings = GraphicsSettingsManager.Instance;
            if (settings == null || !settings.ActiveSettings.m_sunShafts) return;   // setting off: CameraEffects owns enabled
            bool needed = ShaftsVisible(cam, s_shafts);
            if (s_shafts.enabled != needed) s_shafts.enabled = needed;
        }

        // The last rendered probe sits at the reference position of its update; the other one is older and further away
        static void AdaptProbeInterval()
        {
            if (ZNet.instance == null) return;
            ReflectionUpdate ru = s_reflection;
            Vector3 target = ZNet.instance.GetReferencePosition() + Vector3.up * ru.m_reflectionHeight;
            float d = float.MaxValue;
            if (ru.m_probe1 != null) d = Mathf.Min(d, Vector3.Distance(ru.m_probe1.transform.position, target));
            if (ru.m_probe2 != null) d = Mathf.Min(d, Vector3.Distance(ru.m_probe2.transform.position, target));
            ru.m_interval = d > ProbeMoveMeters ? s_vanillaInterval : Mathf.Max(s_vanillaInterval, ProbeIdleInterval);
        }

        static bool ShaftsVisible(Camera cam, SunShafts shafts)
        {
            Transform sun = shafts.sunTransform;
            if (sun == null) return true;                 // vanilla then uses the screen centre
            Vector3 v = cam.WorldToViewportPoint(sun.position);
            if (v.z < 0f) return false;                   // vanilla zeroes the shaft colour when the sun is behind
            float dx = Mathf.Max(0f, Mathf.Max(-v.x, v.x - 1f));
            float dy = Mathf.Max(0f, Mathf.Max(-v.y, v.y - 1f));
            float r = shafts.maxRadius + ShaftsMargin;
            return dx * dx + dy * dy < r * r;
        }

        /// <summary>
        /// ReflectionUpdate.Start: the two ping-pong probes are re-rendered every few seconds; render one cube face per frame
        /// (vanilla prefabs already do), without sun shadows, and less often while the player stands still (OnPreCull).
        /// </summary>
        public static void SliceReflectionProbes(MonoBehaviour updater)
        {
            var ru = (ReflectionUpdate)updater;
            s_reflection = ru;
            s_vanillaInterval = ru.m_interval;
            foreach (ReflectionProbe probe in new[] { ru.m_probe1, ru.m_probe2 })
            {
                if (probe == null) continue;
                ReflectionProbeTimeSlicingMode before = probe.timeSlicingMode;
                float shadowsBefore = probe.shadowDistance;
                if (before != ReflectionProbeTimeSlicingMode.IndividualFaces)
                    probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;
                probe.shadowDistance = 0f;   // a blurred 128 px cube does not show them; each face re-rendered the sun's shadow maps
                Debug.Log($"[NativePatch] reflection probe {probe.name}: {before} -> IndividualFaces, shadowDistance {shadowsBefore} -> 0, resolution {probe.resolution}, " +
                          $"hdr {probe.hdr}, cullingMask 0x{probe.cullingMask:X8}, interval {ru.m_interval} s (idle {ProbeIdleInterval} s)");
            }
        }
    }
}
