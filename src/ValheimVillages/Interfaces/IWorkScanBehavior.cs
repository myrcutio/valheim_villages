namespace ValheimVillages.Interfaces
{
    /// <summary>
    /// Abstraction for work-order scanning. Used by Villager.AI.BehaviorLogic so it does not
    /// depend on NPCs or concrete crafting/farm adapter types.
    /// </summary>
    public interface IWorkScanBehavior
    {
        /// <summary>
        /// Try to find a work order and begin working. May enqueue a scan task and return false
        /// until the task is processed.
        /// </summary>
        /// <param name="ignoreScanInterval">If true, allow a scan even if the last scan was recent.</param>
        /// <returns>True if work was started or a scan was enqueued; false if already working or scan pending.</returns>
        bool TryScanForWork(bool ignoreScanInterval = false);
    }
}
