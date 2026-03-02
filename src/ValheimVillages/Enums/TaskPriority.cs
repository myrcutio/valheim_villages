namespace ValheimVillages.Enums
{
    /// <summary>
    /// Priority tier for queued tasks. The integer value doubles as the
    /// per-tick throughput for that tier (e.g. High = 3 → up to 3 messages/tick).
    /// </summary>
    public enum TaskPriority
    {
        Low = 1,       // Container scans, POI validation -- 1 message/tick
        Medium = 2,    // Work order evaluation, POI discovery -- 2 messages/tick
        High = 3       // Breach detection, quest generation -- 3 messages/tick
    }
}
