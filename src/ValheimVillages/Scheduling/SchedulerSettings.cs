using BepInEx.Configuration;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Tuning + feature flags for the experimental reranker scheduler.
    /// </summary>
    public static class SchedulerSettings
    {
        /// <summary>
        ///     Master switch. While true the reranker runs in LOG-ONLY mode from the
        ///     villager idle hook — it computes a pick each idle tick and logs it, but
        ///     does NOT drive the villager. Flip the dispatch wiring on only once the
        ///     logged picks look sane against what the behavior system chose.
        /// </summary>
        public static bool Enabled = true;

        private static ConfigEntry<bool> s_primaryMode;

        /// <summary>
        ///     Bind <see cref="PrimaryMode" /> to the BepInEx config so it survives hot
        ///     reloads and restarts. A plain static reset to false on every assembly
        ///     reload, silently dropping the scheduler back to log-only after each reload.
        ///     Call once from <c>Plugin.Awake</c>, before anything reads PrimaryMode.
        /// </summary>
        public static void BindConfig(ConfigFile config)
        {
            if (config == null) return;

            s_primaryMode ??= config.Bind("Scheduler", "PrimaryMode", false,
                "When true the dual-encoder scheduler is the PRIMARY work selector: it " +
                "dispatches scheduler-owned tasks (repair, cook-rescue) by directing the " +
                "matching behavior and suppressing those behaviors' self-discovery. Reactive " +
                "behaviors (combat/flee/alarm) still preempt. Persists across reloads.");

            Plugin.Log?.LogInfo($"[SchedulerSettings] PrimaryMode bound = {s_primaryMode.Value}");
        }

        /// <summary>
        ///     When true, the scheduler is the PRIMARY work selector (see <see cref="BindConfig" />).
        ///     Config-backed so it survives hot reloads/restarts; reads false until
        ///     <see cref="BindConfig" /> has run (no throw during the early-boot race).
        /// </summary>
        public static bool PrimaryMode
        {
            get => s_primaryMode?.Value ?? false;
            set
            {
                if (s_primaryMode != null) s_primaryMode.Value = value;
            }
        }

        /// <summary>Minimum seconds between producer scans for a given village.</summary>
        public static float ScanInterval = 3f;

        /// <summary>Two-tower retrieval depth handed to the exact rerank.</summary>
        public static int RetrieveTopM = 8;

        /// <summary>Weight on the task-priority channel in the query embedding.</summary>
        public static float PriorityWeight = 6f;

        /// <summary>
        ///     Behaviors at or above this priority preempt the scheduler even in
        ///     PrimaryMode (combat/flee/alarm = 100). Routine work sits below.
        /// </summary>
        public static int ReactivePriorityFloor = 100;

        /// <summary>Seconds a task assignment is reserved to one villager.</summary>
        public static float ClaimTtl = 20f;
    }
}
