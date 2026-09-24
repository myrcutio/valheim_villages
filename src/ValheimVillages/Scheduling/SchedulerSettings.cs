namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Tuning constants for the dual-encoder scheduler.
    ///
    ///     <para>The scheduler is the ONLY work selector. There is no log-only mode and no
    ///     "primary" switch: every non-reactive task a villager performs is dispatched by
    ///     <see cref="SchedulerDispatcher" /> from the village <see cref="TaskBoard" />.
    ///     Behaviors no longer self-discover work — see
    ///     <see cref="Interfaces.IDirectedBehavior" />.</para>
    /// </summary>
    public static class SchedulerSettings
    {
        /// <summary>Minimum seconds between producer scans for a given village.</summary>
        public static float ScanInterval = 3f;

        /// <summary>Two-tower retrieval depth handed to the exact rerank.</summary>
        public static int RetrieveTopM = 8;

        /// <summary>Weight on the task-priority channel in the query embedding.</summary>
        public static float PriorityWeight = 6f;

        /// <summary>
        ///     Behaviors at or above this priority preempt the scheduler (combat/flee/alarm =
        ///     100). Routine filler (wander/relax) sits below and only runs when the scheduler
        ///     has nothing to dispatch.
        /// </summary>
        public static int ReactivePriorityFloor = 100;

        /// <summary>Seconds a task assignment is reserved to one villager.</summary>
        public static float ClaimTtl = 20f;

        /// <summary>
        ///     Seconds an assignment may stay active before the dispatcher abandons it. The
        ///     claim TTL above only stops OTHER villagers taking the task; nothing else bounds
        ///     how long the holder reads as busy, so a behavior knocked out of its flow (e.g. a
        ///     flee dropping a craft mid-walk to Idle) pinned its villager indefinitely.
        ///     Generous on purpose: a legitimate smelt can sit at the station for 300s alone.
        /// </summary>
        public static float AssignmentCeiling = 600f;

        /// <summary>
        ///     Whether the reranker's residual learns online from dispatch outcomes
        ///     (<see cref="SchedulerTrainer" />). Off leaves the model exactly as loaded, so the
        ///     scheduler runs on its closed-form utility alone.
        /// </summary>
        public static bool TrainingEnabled = true;

        /// <summary>
        ///     SGD step size. Small on purpose: samples arrive one dispatch at a time with no
        ///     batching or replay, so a large rate lets one unlucky fizzle swamp the model.
        /// </summary>
        public static float LearningRate = 0.01f;

        /// <summary>Log every training step. Verbose — for tuning sessions, not normal play.</summary>
        public static bool LogTraining = false;
    }
}
