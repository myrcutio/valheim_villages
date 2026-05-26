using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Minimal interface for resolving crafting/cooking stations from known locations.
    ///     Implemented by both Villager.AI.VillagerAI and legacy AI for StationFinder.
    /// </summary>
    public interface IVillagerStationLookup
    {
        IReadOnlyList<KnownLocation> KnownLocations { get; }
        Vector3 BedPosition { get; }
    }

    /// <summary>
    ///     Extended lookup for work-order handlers (farm context, logging). Implemented by both AI types.
    /// </summary>
    public interface IVillagerWorkContext : IVillagerStationLookup
    {
        string NpcName { get; }

        /// <summary>Current world position (for distance checks).</summary>
        Vector3 Position { get; }
    }
}