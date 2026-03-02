using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Extended ITaskHandler interface with Handle method and VillagerActivityLog support.
    /// Task handler implementations should implement this interface.
    /// The Core ITaskHandler provides the Unity-free base (TaskName).
    /// </summary>
    public interface ITaskHandlerWithLog : ITaskHandler
    {
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
