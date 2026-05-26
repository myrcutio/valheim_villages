using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Abstraction over villager memory (known locations, bed, comfort).
    ///     Implemented by Villager.AI.Memory.VillagerMemory
    ///     so POI discovery and handlers can work with either.
    /// </summary>
    public interface IVillagerMemory
    {
        Vector3 BedPosition { get; set; }
        IReadOnlyList<KnownLocation> KnownLocations { get; }
        void DiscoverLocation(Vector3 position, LocationType type, float comfortValue, bool hasShelter = false);
        void UpdateBestComfort(float comfort, Vector3 position);
        IEnumerable<LocationType> GetMissingLocationTypes();
        IEnumerable<KnownLocation> GetValidatableLocations();
        void RemoveLocation(KnownLocation location);
    }
}