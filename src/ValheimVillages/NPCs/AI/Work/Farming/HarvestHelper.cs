using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work.Farming
{
    /// <summary>
    /// Handles finding mature harvestable crops and picking them for NPC farming.
    /// </summary>
    public static class HarvestHelper
    {
        /// <summary>Default radius to scan for harvestable plants.</summary>
        public const float HarvestScanRadius = 20f;

        // Reflection for private Pickable.m_picked field
        private static readonly FieldInfo s_pickedField =
            typeof(Pickable).GetField("m_picked",
                BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// Find all harvestable Pickable objects near the given position that produce
        /// the specified output item. Returns empty list if none found.
        /// </summary>
        public static List<Pickable> FindHarvestableCrops(
            Vector3 center, float radius, string outputItemName)
        {
            var results = new List<Pickable>();
            var seen = new HashSet<int>();
            var colliders = Physics.OverlapSphere(center, radius);

            foreach (var col in colliders)
            {
                if (col == null) continue;
                var pickable = col.GetComponentInParent<Pickable>();
                if (pickable == null) continue;
                if (!seen.Add(pickable.GetInstanceID())) continue;

                // Skip already-picked items
                if (IsAlreadyPicked(pickable)) continue;

                // Check it produces the right item
                if (pickable.m_itemPrefab == null) continue;
                if (pickable.m_itemPrefab.name != outputItemName) continue;

                // Must be harvestable (check CanBePicked if available)
                if (!pickable.CanBePicked()) continue;

                results.Add(pickable);
            }

            return results;
        }

        /// <summary>
        /// Find any harvestable Pickable near farm locations in the NPC's memory.
        /// Returns the first match with its position, or null if none found.
        /// </summary>
        public static Pickable FindNearestHarvestableCrop(
            VillagerAI ai, string outputItemName, float radius)
        {
            if (ai?.Memory?.KnownLocations == null) return null;

            Pickable nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var loc in ai.Memory.KnownLocations)
            {
                if (loc.Type != LocationType.Farm) continue;

                var crops = FindHarvestableCrops(loc.Position, radius, outputItemName);
                foreach (var crop in crops)
                {
                    float dist = Vector3.Distance(ai.Position, crop.transform.position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = crop;
                    }
                }
            }

            return nearest;
        }

        /// <summary>
        /// Harvest a Pickable crop. Calls Interact which triggers the RPC to
        /// spawn item drops on the ground near the plant.
        /// </summary>
        public static bool HarvestCrop(Pickable pickable, Humanoid npc)
        {
            if (pickable == null || npc == null) return false;
            if (!pickable.CanBePicked()) return false;

            Plugin.Log?.LogInfo(
                $"[Farming] Harvesting {pickable.m_itemPrefab?.name ?? "?"} " +
                $"at {pickable.transform.position}");

            return pickable.Interact(npc, false, false);
        }

        /// <summary>
        /// Count harvestable crops near farm locations for a specific output item.
        /// </summary>
        public static int CountHarvestableCrops(
            VillagerAI ai, string outputItemName, float radius)
        {
            if (ai?.Memory?.KnownLocations == null) return 0;
            int count = 0;
            foreach (var loc in ai.Memory.KnownLocations)
            {
                if (loc.Type != LocationType.Farm) continue;
                count += FindHarvestableCrops(loc.Position, radius, outputItemName).Count;
            }
            return count;
        }

        /// <summary>
        /// Check if a Pickable has already been picked (private m_picked field).
        /// </summary>
        private static bool IsAlreadyPicked(Pickable pickable)
        {
            if (s_pickedField == null) return false;
            return (bool)s_pickedField.GetValue(pickable);
        }
    }
}
