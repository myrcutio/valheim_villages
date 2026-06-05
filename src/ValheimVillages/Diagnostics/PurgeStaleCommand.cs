using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev console command to report and purge hot-reload "zombie" instances:
    ///     MonoBehaviours from a previous assembly that keep running their
    ///     Update/LateUpdate after a reload and pollute live diagnostics.
    ///     Runs only the stale/orphan sweeps (never the [RegisterCleanup] reset),
    ///     so it is safe to call mid-session to get a clean slate for testing.
    /// </summary>
    internal static class PurgeStaleCommand
    {
        [DevCommand(
            "Report + purge stale (old-assembly) mod instances left by hot reloads",
            Name = "vv_purge_stale")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var report = HotReloadHelper.PurgeStaleObjects();
            Console.instance?.Print(report);
            Plugin.Log?.LogWarning(report);
        }
    }
}
