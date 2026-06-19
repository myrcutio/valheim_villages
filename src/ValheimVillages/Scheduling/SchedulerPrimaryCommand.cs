using ValheimVillages.Attributes;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Toggle (or set) <see cref="SchedulerSettings.PrimaryMode" /> at runtime so the
    ///     dual-encoder scheduler can be switched between log-only and primary-driver
    ///     without a rebuild. Usage: <c>vv_scheduler_primary [on|off]</c> (no arg toggles).
    /// </summary>
    public static class SchedulerPrimaryCommand
    {
        [DevCommand("Toggle/set scheduler PrimaryMode (scheduler drives villagers). Usage: vv_scheduler_primary [on|off]",
            Name = "vv_scheduler_primary")]
        public static void Toggle(Terminal.ConsoleEventArgs args)
        {
            if (args.Length >= 2)
            {
                var v = args[1].ToLowerInvariant();
                SchedulerSettings.PrimaryMode = v == "on" || v == "true" || v == "1";
            }
            else
            {
                SchedulerSettings.PrimaryMode = !SchedulerSettings.PrimaryMode;
            }

            var msg = $"[vv_scheduler_primary] PrimaryMode={SchedulerSettings.PrimaryMode} " +
                      $"(Enabled={SchedulerSettings.Enabled}, topM={SchedulerSettings.RetrieveTopM})";
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
