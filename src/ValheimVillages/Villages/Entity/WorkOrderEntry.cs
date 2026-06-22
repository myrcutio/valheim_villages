namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     One work-order config record owned by a <see cref="Village" /> (host-authoritative),
    ///     replacing the quota that used to live in a chest token's <c>m_customData</c> — which
    ///     was clobbered by the chest's owner ping-pong. Keyed within the village by
    ///     (<see cref="Station" />, <see cref="Item" />).
    ///     <para><see cref="Item" /> is the OUTPUT prefab name (the order identity);
    ///     <see cref="Max" /> is the quota cap the scan enforces (<see cref="Min" /> is carried
    ///     for UI/logs but does not gate). The legacy <c>wo_range</c> string is intentionally
    ///     dropped — it was a derived display cache that nothing read.</para>
    /// </summary>
    public readonly struct WorkOrderEntry
    {
        public readonly string Station; // wo_station (CraftingStation.m_name)
        public readonly string Item; // wo_item (output prefab name) — identity
        public readonly string ItemDisplay; // wo_item_name (m_shared.m_name token) — UI only
        public readonly int Min; // wo_min
        public readonly int Max; // wo_max — the quota cap the scan enforces

        public WorkOrderEntry(string station, string item, string itemDisplay, int min, int max)
        {
            Station = station;
            Item = item;
            ItemDisplay = itemDisplay;
            Min = min;
            Max = max;
        }
    }
}
