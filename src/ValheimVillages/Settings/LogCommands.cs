using ValheimVillages.Attributes;

namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Dev console commands for toggling runtime logging verbosity.
    ///     Companion to <see cref="LogSettings" />.
    /// </summary>
    internal static class LogCommands
    {
        [DevCommand("Toggle high-volume NavMesh probe-area logging on/off", Name = "vv_log_navmesh")]
        public static void ToggleVerboseNavMesh(Terminal.ConsoleEventArgs args)
        {
            LogSettings.VerboseNavMesh = !LogSettings.VerboseNavMesh;
            var state = LogSettings.VerboseNavMesh ? "ON" : "OFF";
            var msg = $"[LogSettings] VerboseNavMesh = {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        [DevCommand("Toggle per-container ingredient-scan logging on/off", Name = "vv_log_ingredients")]
        public static void ToggleVerboseIngredientScan(Terminal.ConsoleEventArgs args)
        {
            LogSettings.VerboseIngredientScan = !LogSettings.VerboseIngredientScan;
            var state = LogSettings.VerboseIngredientScan ? "ON" : "OFF";
            var msg = $"[LogSettings] VerboseIngredientScan = {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        [DevCommand("Toggle per-item ItemDrop.Awake spawn logging on/off", Name = "vv_log_itemspawns")]
        public static void ToggleVerboseItemSpawns(Terminal.ConsoleEventArgs args)
        {
            LogSettings.VerboseItemSpawns = !LogSettings.VerboseItemSpawns;
            var state = LogSettings.VerboseItemSpawns ? "ON" : "OFF";
            var msg = $"[LogSettings] VerboseItemSpawns = {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}