using UnityEngine;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Minimal villager context for resolving village stations/PoIs by
    ///     position (anchored on the villager's bed). Implemented by
    ///     Villager.AI.VillagerAI.
    /// </summary>
    public interface IVillagerStationLookup
    {
        Vector3 HomeAnchor { get; }
    }

    /// <summary>
    ///     Extended lookup for work-order handlers (farm context, logging).
    /// </summary>
    public interface IVillagerWorkContext : IVillagerStationLookup
    {
        string NpcName { get; }

        /// <summary>Current world position (for distance checks).</summary>
        Vector3 Position { get; }
    }
}
