using UnityEngine;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Abstraction over per-villager remembered state: the home bed and the
    ///     best comfort the villager has personally experienced. Shared points of
    ///     interest and stations now live in the village-level registries
    ///     (VillagePoiRegistry / VillageStationRegistry), not here.
    ///     Implemented by Villager.AI.Memory.VillagerMemory.
    /// </summary>
    public interface IVillagerMemory
    {
        Vector3 BedPosition { get; set; }
        void UpdateBestComfort(float comfort, Vector3 position);
    }
}
