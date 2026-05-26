using System;
using System.Collections.Generic;
using ValheimVillages.Enums;

namespace ValheimVillages.Schemas
{
    /// <summary>
    ///     A single task message to be processed by the global task queue.
    /// </summary>
    public class VillagerTask
    {
        /// <summary>Handler-specific parameters, keyed by name.</summary>
        public Dictionary<string, string> Attributes;

        /// <summary>
        ///     Invoked synchronously on the main thread when the task completes.
        ///     Handlers can safely mutate Unity state inside the callback.
        /// </summary>
        public Action<TaskResult> Callback;

        /// <summary>Time.time when the task was enqueued.</summary>
        public float CreatedAt;

        /// <summary>Handler name, e.g. "container_scan", "breach_check".</summary>
        public string Name;

        /// <summary>
        ///     Earliest Time.time at which this task may be processed.
        ///     Zero (default) means no deferral. DrainQueue re-enqueues
        ///     tasks whose NotBefore has not yet elapsed.
        /// </summary>
        public float NotBefore;

        /// <summary>Which priority tier this task belongs to.</summary>
        public TaskPriority Priority;

        /// <summary>Number of times this task has been retried after failure.</summary>
        public int RetryCount;

        /// <summary>
        ///     Reference to the object that triggered this task.
        ///     Typically a villager GUID, or a composite key like "guard_id:waypoint_idx".
        /// </summary>
        public string SourceId;

        /// <summary>Maximum seconds before the task expires without processing.</summary>
        public float TimeoutSeconds;
    }

    /// <summary>
    ///     Tuning constants for the task queue system.
    /// </summary>
    public static class TaskSettings
    {
        /// <summary>Maximum retry attempts before a task is dead-lettered.</summary>
        public const int MaxRetries = 3;

        /// <summary>Default timeout if none is specified on the task.</summary>
        public const float DefaultTimeoutSeconds = 30f;

        /// <summary>Maximum entries in the dead letter queue before oldest are evicted.</summary>
        public const int MaxDeadLetterSize = 50;

        /// <summary>Maximum activity log entries stored per villager before trimming.</summary>
        public const int MaxActivityLogEntriesPerVillager = 100;
    }
}