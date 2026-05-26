namespace ValheimVillages.TaskQueue.ActivityLog
{
    /// <summary>
    ///     A single entry in the per-villager activity log, recording one state-changing
    ///     action taken by a task handler.
    /// </summary>
    public class ActivityLogEntry
    {
        /// <summary>Action verb (e.g. "remove_item", "craft_item", "path_to").</summary>
        public string Action;

        /// <summary>
        ///     Set to true after a ZDO save confirms this entry has been persisted.
        ///     Committed entries can be trimmed to keep the log bounded.
        /// </summary>
        public bool Committed;

        /// <summary>
        ///     Human-readable description of what happened.
        ///     e.g. "removed 3 carrots from container at (10,5,20)"
        /// </summary>
        public string Description;

        /// <summary>Which task generated this entry (e.g. "work_order_scan").</summary>
        public string TaskName;

        /// <summary>Time.time when the action was recorded.</summary>
        public float Timestamp;

        /// <summary>Villager GUID that this action relates to.</summary>
        public string VillagerId;
    }
}