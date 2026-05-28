using System.Globalization;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev commands to fire orchestrated captures on demand. Equivalent to
    ///     what repartition does automatically: teleports the player to an
    ///     anchor, looks straight down, hides the HUD, snaps the PNG, and
    ///     restores state in a <c>finally</c>. Useful when reproducing a
    ///     transient in-game condition without having to also trigger a hot
    ///     reload or repartition.
    /// </summary>
    internal static class CaptureCommand
    {
        [DevCommand("Capture an orchestrated village screenshot + diagnostics sidecar",
            Name = "vv_capture")]
        public static void Capture()
        {
            DebugLog.Capture("manual");
            const string msg = "[vv_capture] Enqueued orchestrated capture (manual trigger)";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        /// <summary>
        ///     Capture an orchestrated screenshot at a specific world XZ with
        ///     a tighter clearance — sibling to <c>vv_capture</c> for incident-
        ///     style framing. Y is sampled from solid terrain at (x, z) so the
        ///     camera lands at terrain Y + clearance instead of sea-level +
        ///     clearance, which matters at higher-altitude villages.
        /// </summary>
        [DevCommand("Capture at (x, z) [clearance=10m]: vv_capture_at <x> <z> [clearance]",
            Name = "vv_capture_at")]
        public static void CaptureAt(string xArg, string zArg, string clearanceArg = null)
        {
            if (!float.TryParse(xArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(zArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                Report($"could not parse coords '{xArg}' '{zArg}' as floats");
                return;
            }

            var clearance = 10f;
            if (!string.IsNullOrEmpty(clearanceArg) &&
                !float.TryParse(clearanceArg, NumberStyles.Float, CultureInfo.InvariantCulture, out clearance))
            {
                Report($"could not parse clearance '{clearanceArg}' as float, using 10m");
                clearance = 10f;
            }

            // Sample terrain Y at (x, z) so the camera lands above the actual
            // ground, not at sea-level + clearance. Falls back to 0 if the
            // probe fails — capture will still write, just framed lower.
            var anchorY = 0f;
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetSolidHeight(new Vector3(x, 500f, z), out var terrainY, 550))
            {
                anchorY = terrainY;
            }

            var req = CaptureRequest.ForIncident("manual_at",
                incidentSubdir: "", baseName: "last_capture",
                anchor: new Vector3(x, anchorY, z), clearance: clearance);
            DebugLog.Capture(req);
            Report($"enqueued at ({x:F1},{anchorY:F1},{z:F1}) clearance={clearance:F1}m");
        }

        private static void Report(string state)
        {
            var msg = $"[vv_capture_at] {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
