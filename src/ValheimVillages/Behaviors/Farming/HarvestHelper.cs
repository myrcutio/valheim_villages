using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    ///     Handles finding mature harvestable crops and picking them for NPC farming.
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
        ///     Find all harvestable Pickable objects near the given position that produce
        ///     the specified output item. Returns empty list if none found.
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
        ///     Find any harvestable Pickable near the village's Farm PoIs.
        ///     Returns the nearest match, or null if none found.
        /// </summary>
        public static Pickable FindNearestHarvestableCrop(
            VillagerAI ai, string outputItemName, float radius)
        {
            return FindNearestHarvestableCrop(ai.transform.position, outputItemName, radius);
        }

        /// <summary>
        ///     Find a harvestable Pickable near <paramref name="currentPosition" />.
        ///     Scans directly around that position AND around the village's Farm
        ///     PoIs. The direct scan is essential: a GROWN crop is a Pickable_X
        ///     (e.g. Pickable_Onion) that no longer registers as a Farm PoI — only
        ///     saplings / cultivated ground do — so a Farm-PoI-only search misses
        ///     exactly the crops that are ready to harvest.
        /// </summary>
        public static Pickable FindNearestHarvestableCrop(
            Vector3 currentPosition, string outputItemName, float radius)
        {
            Pickable nearest = null;
            var nearestDist = float.MaxValue;

            var anchors = new List<Vector3> { currentPosition };
            foreach (var loc in VillagePoiRegistry.GetPois(currentPosition, LocationType.Farm))
                anchors.Add(loc.Position);

            foreach (var anchor in anchors)
                foreach (var crop in FindHarvestableCrops(anchor, radius, outputItemName))
                {
                    var dist = Vector3.Distance(currentPosition, crop.transform.position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = crop;
                    }
                }

            return nearest;
        }

        /// <summary>
        ///     Harvest a Pickable crop. Calls Interact which triggers the RPC to
        ///     spawn item drops on the ground near the plant.
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
        ///     Count harvestable crops near farm locations for a specific output item.
        /// </summary>
        public static int CountHarvestableCrops(
            VillagerAI ai, string outputItemName, float radius)
        {
            var count = 0;
            foreach (var loc in VillagePoiRegistry.GetPois(ai.transform.position, LocationType.Farm))
                count += FindHarvestableCrops(loc.Position, radius, outputItemName).Count;
            return count;
        }

        /// <summary>
        ///     Check if a Pickable has already been picked (private m_picked field).
        /// </summary>
        private static bool IsAlreadyPicked(Pickable pickable)
        {
            if (s_pickedField == null) return false;
            return (bool)s_pickedField.GetValue(pickable);
        }
    }
}