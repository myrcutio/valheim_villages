using System.Collections.Generic;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI
{
    /// <summary>
    /// Static registry for VillagerAI instances, keyed by unique ID.
    /// Inspired by MobAILib's MobManager pattern.
    /// </summary>
    public static class VillagerAIManager
    {
        /// <summary>
        /// All active villager AI instances, keyed by their unique ID.
        /// </summary>
        public static readonly Dictionary<string, VillagerAI> ActiveVillagers = new();

        /// <summary>
        /// Pending registrations (legacy): uniqueId -> bed position for villagers registered
        /// before their AI component exists. The primary path registers via RegisterActive
        /// from VillagerAI.Awake (component lifecycle).
        /// </summary>
        private static readonly Dictionary<string, UnityEngine.Vector3> s_pendingRegistrations = new();

        /// <summary>
        /// Register an active VillagerAI instance. Called from VillagerAI.Awake when the
        /// Villager component adds VillagerAI (component lifecycle; no MonsterAI).
        /// </summary>
        public static void RegisterActive(VillagerAI ai)
        {
            if (ai == null || string.IsNullOrEmpty(ai.UniqueId)) return;
            ActiveVillagers[ai.UniqueId] = ai;
            Plugin.Log?.LogInfo($"[VillagerAIManager] Registered villager {ai.UniqueId} (active)");
        }

        /// <summary>
        /// Legacy: Register a villager by ID and bed position (e.g. before AI component exists).
        /// The primary path uses Villager + VillagerAI components and RegisterActive from Awake.
        /// </summary>
        public static void Register(string uniqueId, UnityEngine.Vector3 bedPosition)
        {
            if (string.IsNullOrEmpty(uniqueId)) return;

            s_pendingRegistrations[uniqueId] = bedPosition;
            Plugin.Log?.LogInfo($"[VillagerAIManager] Registered villager {uniqueId} (pending)");
        }

        /// <summary>
        /// Unregister a villager.
        /// </summary>
        public static void Unregister(string uniqueId)
        {
            ActiveVillagers.Remove(uniqueId);
            s_pendingRegistrations.Remove(uniqueId);
        }

        /// <summary>
        /// Unregister and destroy reference when a villager is destroyed.
        /// </summary>
        public static void Unregister(VillagerAI ai)
        {
            if (ai != null)
                Unregister(ai.UniqueId);
        }

        /// <summary>
        /// Check if a unique ID is registered (either pending or active).
        /// </summary>
        public static bool IsRegistered(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return false;
            return ActiveVillagers.ContainsKey(uniqueId) || s_pendingRegistrations.ContainsKey(uniqueId);
        }

        /// <summary>
        /// Get unique bed positions from all active villagers.
        /// Reads the authoritative <see cref="Villager.BedPosition"/> from each
        /// villager's component and deduplicates positions within 1m of each other.
        /// </summary>
        public static List<UnityEngine.Vector3> GetAllBedPositions()
        {
            var list = new List<UnityEngine.Vector3>();
            foreach (var ai in ActiveVillagers.Values)
            {
                var villager = ai?.Villager;
                if (villager == null) continue;
                var pos = villager.BedPosition;
                if (pos == UnityEngine.Vector3.zero) continue;

                bool duplicate = false;
                foreach (var existing in list)
                {
                    if ((existing - pos).sqrMagnitude < 1f)
                    { duplicate = true; break; }
                }
                if (!duplicate)
                    list.Add(pos);
            }
            return list;
        }

        /// <summary>
        /// Clear all registrations (e.g. on world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            ActiveVillagers.Clear();
            s_pendingRegistrations.Clear();
        }
    }
}
