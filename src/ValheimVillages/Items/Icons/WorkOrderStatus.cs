namespace ValheimVillages.Items.Icons
{
    /// <summary>
    /// Visual status of a work order for icon overlay purposes.
    /// </summary>
    public enum WorkOrderStatus
    {
        /// <summary>No status determined yet, or work order is in player inventory.</summary>
        Pending,
        /// <summary>A villager is actively working on this work order.</summary>
        InProgress,
        /// <summary>Output quantity has reached or exceeded the max quota.</summary>
        Completed,
        /// <summary>Cannot be fulfilled (no recipe, no station, etc.).</summary>
        Unworkable
    }
}
