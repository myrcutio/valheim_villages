using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Interface for task handlers that process queued VillagerTasks.
    /// Each handler is registered by its TaskName in the TaskHandlerRegistry.
    /// </summary>
    public interface ITaskHandler
    {
        /// <summary>
        /// The task name this handler processes (e.g. "container_scan", "breach_check").
        /// Must match the VillagerTask.Name of tasks routed to this handler.
        /// </summary>
        string TaskName { get; }

        /// <summary>
        /// Process a task. Called synchronously on the main thread.
        /// Handlers should record any state-changing actions to the activity log.
        /// </summary>
        /// <param name="task">The task to process.</param>
        /// <param name="activityLog">Activity log for recording actions taken.</param>
        /// <returns>Result indicating success or failure.</returns>
        TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog);
    }
}
