namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Base interface for task handlers.
    ///     Core version with the Unity-free contract (TaskName only).
    ///     The mod assembly defines ITaskHandlerWithLog adding the Handle method.
    /// </summary>
    public interface ITaskHandler
    {
        /// <summary>
        ///     The task name this handler processes (e.g. "container_scan", "breach_check").
        ///     Must match the VillagerTask.Name of tasks routed to this handler.
        /// </summary>
        string TaskName { get; }
    }
}