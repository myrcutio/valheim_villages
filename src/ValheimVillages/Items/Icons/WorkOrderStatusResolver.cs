using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Determines the visual status of a work order item by checking
    ///     container output quantities, active villager work, and recipe availability.
    ///     Accepts a pre-scanned container list to avoid redundant physics queries
    ///     when resolving many work orders at the same position.
    /// </summary>
    public static class WorkOrderStatusResolver
    {
        /// <summary>
        ///     Resolve the status of a work order item.
        /// </summary>
        /// <param name="item">The work order item with custom data.</param>
        /// <param name="containerPos">
        ///     Position used for container scanning if <paramref name="containers" />
        ///     is null. Pass null when in player inventory away from village.
        /// </param>
        /// <param name="containers">
        ///     Pre-scanned nearby containers. When provided, skips the physics
        ///     query (avoids O(n) scans for n work orders at the same spot).
        /// </param>
        public static WorkOrderStatus Resolve(
            ItemDrop.ItemData item,
            Vector3? containerPos,
            List<Container> containers = null)
        {
            if (item?.m_customData == null) return WorkOrderStatus.Pending;

            if (!item.m_customData.TryGetValue("wo_item", out var itemPrefab)
                || string.IsNullOrEmpty(itemPrefab))
                return WorkOrderStatus.Pending;

            if (!item.m_customData.TryGetValue("wo_station", out var station)
                || string.IsNullOrEmpty(station))
                return WorkOrderStatus.Pending;

            var max = 10;
            if (item.m_customData.TryGetValue("wo_max", out var maxStr))
                int.TryParse(maxStr, out max);

            // Check if a villager is actively working this exact order
            if (IsBeingWorked(itemPrefab))
                return WorkOrderStatus.InProgress;

            // Lazily scan containers if not pre-supplied
            if (containers == null && containerPos.HasValue)
                containers = ContainerScanner.FindNearbyContainers(
                    containerPos.Value, WorkSettings.ChestScanRadius);

            if (containers != null && containers.Count > 0)
            {
                var existing = ContainerScanner.CountAcrossContainers(
                    containers, itemPrefab);
                if (existing >= max)
                    return WorkOrderStatus.Completed;

                if (!HasRecipe(itemPrefab, station))
                    return WorkOrderStatus.Unworkable;
            }
            else
            {
                // No containers available: just check recipe existence
                if (!HasRecipe(itemPrefab, station))
                    return WorkOrderStatus.Unworkable;
            }

            return WorkOrderStatus.Pending;
        }

        private static bool IsBeingWorked(string itemPrefab)
        {
            foreach (var kvp in VillagerAIManager.ActiveVillagers)
            {
                var adapter = kvp.Value?.CraftingBehavior;
                var cb = adapter?.Crafting;
                if (cb == null || !cb.IsWorking) continue;
                if (cb.CurrentItemPrefab == itemPrefab)
                    return true;
            }

            return false;
        }

        private static bool HasRecipe(string itemPrefab, string station)
        {
            return StationMatcher.FindRecipe(itemPrefab, station) != null;
        }
    }
}