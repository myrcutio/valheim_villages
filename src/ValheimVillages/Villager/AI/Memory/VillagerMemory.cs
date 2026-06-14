using System;
using UnityEngine;
using ValheimVillages.Interfaces;

namespace ValheimVillages.Villager.AI.Memory
{
    /// <summary>
    ///     Per-villager remembered state: the home bed and the best comfort the
    ///     villager has personally experienced. Shared points of interest
    ///     (fires, tables, farms) and stations are NOT stored here anymore —
    ///     they live in the village-level registries (<c>VillagePoiRegistry</c> /
    ///     <c>VillageStationRegistry</c>) and are looked up by position. Persisted
    ///     to ZDO for save/load across sessions.
    /// </summary>
    public class VillagerMemory : IVillagerMemory
    {
        public VillagerMemory(Vector3 bedPosition)
        {
            HomeAnchor = bedPosition;
        }

        /// <summary>Highest comfort level experienced.</summary>
        public float BestComfortLevel { get; set; }

        /// <summary>Position where best comfort was experienced.</summary>
        public Vector3? BestComfortPosition { get; set; }

        /// <summary>The NPC's home bed position.</summary>
        public Vector3 HomeAnchor { get; set; }

        /// <summary>Update best comfort level if current is higher.</summary>
        public void UpdateBestComfort(float comfort, Vector3 position)
        {
            if (comfort > BestComfortLevel)
            {
                BestComfortLevel = comfort;
                BestComfortPosition = position;
            }
        }

        #region ZDO Persistence

        private const string ZdoKeyBestComfort = "vv_memory_best_comfort";
        private const string ZdoKeyBestComfortPos = "vv_memory_best_comfort_pos";

        /// <summary>Save memory to ZDO for persistence.</summary>
        public void SaveToZDO(ZDO zdo)
        {
            if (zdo == null) return;

            try
            {
                zdo.Set(ZdoKeyBestComfort, BestComfortLevel);
                if (BestComfortPosition.HasValue) zdo.Set(ZdoKeyBestComfortPos, BestComfortPosition.Value);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to save villager memory: {ex.Message}");
            }
        }

        /// <summary>Load memory from ZDO.</summary>
        public void LoadFromZDO(ZDO zdo)
        {
            if (zdo == null) return;

            try
            {
                BestComfortLevel = zdo.GetFloat(ZdoKeyBestComfort);

                var comfortPos = zdo.GetVec3(ZdoKeyBestComfortPos, Vector3.zero);
                if (comfortPos != Vector3.zero) BestComfortPosition = comfortPos;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to load villager memory: {ex.Message}");
            }
        }

        #endregion
    }
}
