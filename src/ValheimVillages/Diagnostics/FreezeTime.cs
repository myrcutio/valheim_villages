using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Day/night cycle freeze for development. Valheim's <c>EnvMan</c>
    ///     exposes <c>m_debugTimeOfDay</c> + <c>m_debugTime</c> for exactly
    ///     this — when the bool is set, the global time-of-day is pinned to
    ///     the float (0..1, where 0.5 is noon).
    ///
    ///     <para>Auto-freezes at noon on plugin load so screenshots and live
    ///     debugging happen under consistent, well-lit conditions instead of
    ///     waiting for the game's slow night phase to pass. Toggle off with
    ///     <c>vv_freezetime</c> (no arg = unfreeze); set a specific time with
    ///     <c>vv_freezetime 0.25</c> (morning), <c>0.75</c> (evening), etc.</para>
    ///
    ///     <para><b>TODO before final release:</b> Remove
    ///     <see cref="AutoFreezeOnLoad"/> or gate it behind a debug-only
    ///     config flag — shipping with the day cycle frozen would be a
    ///     gameplay regression. The console command itself is harmless and
    ///     can stay as a dev-tool.</para>
    /// </summary>
    internal static class FreezeTime
    {
        /// <summary>
        ///     Time-of-day to pin to when <c>m_debugTimeOfDay</c> is set.
        ///     0.5 = noon. Matches the default the auto-freeze applies on
        ///     plugin load.
        /// </summary>
        private const float DefaultFrozenTime = 0.5f;

        public static bool AutoFreezeOnLoad => false; // disabled: was interfering with live MCP-driven iteration

        /// <summary>
        ///     Apply <see cref="DefaultFrozenTime"/> to EnvMan on plugin load.
        ///     Called from <c>Plugin.Awake</c>. No-op if EnvMan isn't alive
        ///     yet (main-menu reload) — the user can re-fire with
        ///     <c>vv_freezetime</c> once in-world.
        /// </summary>
        public static void ApplyAutoFreeze()
        {
            if (EnvMan.instance == null)
            {
                if (AutoFreezeOnLoad)
                    Plugin.Log?.LogInfo(
                        "[FreezeTime] Auto-freeze deferred: EnvMan not yet alive. " +
                        "Run vv_freezetime once in-world to apply.");
                return;
            }

            // Normalise the freeze state to match AutoFreezeOnLoad on every (re)load.
            // EnvMan persists across hot reloads, so a freeze applied by an earlier
            // assembly would otherwise linger even after disabling auto-freeze — this
            // actively clears it.
            SetFrozen(AutoFreezeOnLoad, DefaultFrozenTime);
            Plugin.Log?.LogInfo(AutoFreezeOnLoad
                ? $"[FreezeTime] Auto-frozen at time={DefaultFrozenTime:F2} on load."
                : "[FreezeTime] Day/night cycle left running (auto-freeze disabled).");
        }

        [DevCommand("Freeze the day/night cycle (no arg=unfreeze; or pass a time 0..1)",
            Name = "vv_freezetime")]
        public static void Toggle(string arg = null)
        {
            if (EnvMan.instance == null)
            {
                const string err = "[vv_freezetime] EnvMan not available — no world loaded?";
                Console.instance?.Print(err);
                Plugin.Log?.LogWarning(err);
                return;
            }

            if (string.IsNullOrEmpty(arg))
            {
                // Toggle: if currently frozen, unfreeze; else freeze at default.
                var currentlyFrozen = EnvMan.instance.m_debugTimeOfDay;
                if (currentlyFrozen)
                {
                    SetFrozen(false, 0f);
                    Report($"unfrozen (was at time={EnvMan.instance.m_debugTime:F2})");
                }
                else
                {
                    SetFrozen(true, DefaultFrozenTime);
                    Report($"frozen at time={DefaultFrozenTime:F2} (noon)");
                }

                return;
            }

            if (!float.TryParse(arg, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var t))
            {
                Report($"could not parse '{arg}' as a time-of-day (0..1)");
                return;
            }

            t = UnityEngine.Mathf.Clamp01(t);
            SetFrozen(true, t);
            Report($"frozen at time={t:F2}");
        }

        private static void SetFrozen(bool frozen, float timeOfDay)
        {
            EnvMan.instance.m_debugTimeOfDay = frozen;
            if (frozen) EnvMan.instance.m_debugTime = timeOfDay;
        }

        private static void Report(string state)
        {
            var msg = $"[vv_freezetime] {state}";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
