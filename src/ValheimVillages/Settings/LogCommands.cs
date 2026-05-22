using ValheimVillages.Attributes;

namespace ValheimVillages.Settings
{
    /// <summary>
    /// Dev console commands for toggling runtime logging verbosity.
    /// Companion to <see cref="LogSettings"/>.
    /// </summary>
    internal static class LogCommands
    {
        [DevCommand("Toggle high-volume NavMesh probe-area logging on/off", Name = "vv_log_navmesh")]
        public static void ToggleVerboseNavMesh(Terminal.ConsoleEventArgs args)
        {
            LogSettings.VerboseNavMesh = !LogSettings.VerboseNavMesh;
            string state = LogSettings.VerboseNavMesh ? "ON" : "OFF";
            string msg = $"[LogSettings] VerboseNavMesh = {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
