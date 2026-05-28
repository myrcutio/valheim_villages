using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev command to fire an orchestrated capture on demand. Equivalent to
    ///     what hot-reload does automatically: teleports the player to the
    ///     seed-bed anchor, looks straight down, hides the HUD, snaps the PNG,
    ///     and restores state in a <c>finally</c>. Useful when reproducing a
    ///     transient in-game condition (a stalled villager, a particular path
    ///     failure) without having to also trigger a hot reload.
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
    }
}
