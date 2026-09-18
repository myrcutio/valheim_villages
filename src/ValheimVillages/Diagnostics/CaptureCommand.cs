using System.Globalization;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev command to fire an orchestrated capture on demand. Equivalent to
    ///     what repartition used to do automatically: teleports the player to an
    ///     anchor, looks straight down, hides the HUD, snaps the PNG, and
    ///     restores state in a <c>finally</c>. Useful when reproducing a
    ///     transient in-game condition without having to also trigger a hot
    ///     reload or repartition.
    ///     <para>
    ///         Absorbed the former <c>vv_capture_at</c>. The two forms are not the same
    ///         request with a different origin, so the defaults differ: with no args the
    ///         capture anchors on the nearest village seed anchor at 45m clearance and
    ///         includes the diagnostics sidecar; with explicit coords it anchors there at
    ///         10m and omits diagnostics (incident-style framing).
    ///     </para>
    /// </summary>
    internal static class CaptureCommand
    {
        private const float CoordClearanceDefault = 10f;

        [DevCommand(
            "Capture an orchestrated village screenshot + diagnostics sidecar. " +
            "vv_capture [x z [clearance=10]] — no args = village seed anchor at 45m",
            Name = "vv_capture")]
        public static void Capture(Terminal.ConsoleEventArgs args)
        {
            // No coords: the seed-anchor form, which also carries the diagnostics sidecar.
            if (args == null || args.Length < 3)
            {
                DebugLog.Capture("manual");
                Report("enqueued orchestrated capture at the village seed anchor (manual trigger)");
                return;
            }

            var xArg = args[1];
            var zArg = args[2];
            var clearanceArg = args.Length > 3 ? args[3] : null;

            if (!float.TryParse(xArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(zArg, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                Report($"could not parse coords '{xArg}' '{zArg}' as floats");
                return;
            }

            var clearance = CoordClearanceDefault;
            if (!string.IsNullOrEmpty(clearanceArg) &&
                !float.TryParse(clearanceArg, NumberStyles.Float, CultureInfo.InvariantCulture, out clearance))
            {
                Report($"could not parse clearance '{clearanceArg}' as float, using {CoordClearanceDefault:F0}m");
                clearance = CoordClearanceDefault;
            }

            // Sample terrain Y at (x, z) so the camera lands above the actual
            // ground, not at sea-level + clearance. Falls back to 0 if the
            // probe fails — capture will still write, just framed lower.
            var anchorY = 0f;
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetSolidHeight(new Vector3(x, 500f, z), out var terrainY, 550))
                anchorY = terrainY;

            var req = CaptureRequest.ForIncident("manual_at",
                incidentSubdir: "", baseName: "last_capture",
                anchor: new Vector3(x, anchorY, z), clearance: clearance);
            DebugLog.Capture(req);
            Report($"enqueued at ({x:F1},{anchorY:F1},{z:F1}) clearance={clearance:F1}m");
        }

        private static void Report(string state)
        {
            var msg = $"[vv_capture] {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
