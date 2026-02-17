using System.Collections.Generic;
using ValheimVillages.Core.Attributes;

namespace ValheimVillages.NPCs.AI
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
        /// Pending registrations: villagers that have been spawned but not yet
        /// had their AI created (happens on first BaseAI.UpdateAI tick).
        /// Key: uniqueId, Value: bed position.
        /// </summary>
        private static readonly Dictionary<string, UnityEngine.Vector3> s_pendingRegistrations = new();

        /// <summary>
        /// Register a villager to use our custom AI.
        /// The AI instance is created lazily on the first UpdateAI tick.
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
        /// Check if a unique ID is registered (either pending or active).
        /// </summary>
        public static bool IsRegistered(string uniqueId)
        {
            if (string.IsNullOrEmpty(uniqueId)) return false;
            return ActiveVillagers.ContainsKey(uniqueId) || s_pendingRegistrations.ContainsKey(uniqueId);
        }

        /// <summary>
        /// Get or create the VillagerAI for a registered villager.
        /// Called by the Harmony patch on MonsterAI.UpdateAI.
        /// </summary>
        public static VillagerAI GetOrCreate(string uniqueId, MonsterAI monsterAI)
        {
            if (ActiveVillagers.TryGetValue(uniqueId, out var existing))
            {
                if (existing.HasInstance)
                    return existing;

                // Instance lost (e.g. hot reload), recreate
                ActiveVillagers.Remove(uniqueId);
            }

            // Check pending registrations
            if (!s_pendingRegistrations.TryGetValue(uniqueId, out var bedPosition))
                return null;

            // Create the AI instance
            var villagerAI = new VillagerAI(monsterAI, bedPosition, uniqueId);
            ActiveVillagers[uniqueId] = villagerAI;
            s_pendingRegistrations.Remove(uniqueId);

            Plugin.Log?.LogInfo($"[VillagerAIManager] Created VillagerAI for {uniqueId}");
            return villagerAI;
        }

        /// <summary>
        /// Get all known bed positions (active villagers and pending registrations).
        /// Used e.g. by HNA partition to define village bounds (15m around each bed).
        /// </summary>
        public static List<UnityEngine.Vector3> GetAllBedPositions()
        {
            var list = new List<UnityEngine.Vector3>();
            foreach (var ai in ActiveVillagers.Values)
            {
                if (ai?.Memory != null)
                    list.Add(ai.Memory.BedPosition);
            }
            foreach (var pos in s_pendingRegistrations.Values)
                list.Add(pos);
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
