using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Items;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Utility for scanning nearby containers for work orders and ingredients.
    /// </summary>
    public static class ContainerScanner
    {
        /// <summary>
        /// Find all Container components within the given radius of a position.
        /// </summary>
        public static List<Container> FindNearbyContainers(Vector3 center, float radius)
        {
            var result = new List<Container>();
            foreach (var container in PhysicsHelper.GetAllInRadius<Container>(center, radius))
            {
                if (container == null || result.Contains(container)) continue;
                var nview = container.GetComponent<ZNetView>();
                if (nview == null || nview.GetZDO() == null) continue;
                result.Add(container);
            }
            return result;
        }

        /// <summary>
        /// Scan containers for all work orders that match the NPC's station type.
        /// Caller should try each in turn until one passes full validation (recipe, ingredients, station).
        /// </summary>
        public static List<WorkOrderMatch> FindAllWorkOrders(List<Container> containers, NpcType npcType)
        {
            var matches = new List<WorkOrderMatch>();
            foreach (var container in containers)
            {
                var inventory = container.GetInventory();
                if (inventory == null) continue;

                var allItems = inventory.GetAllItems();
                Plugin.Log?.LogDebug(
                    $"[ContainerScan] Checking container '{container.m_name}' " +
                    $"at {container.transform.position} with {allItems.Count} items");

                foreach (var item in allItems)
                {
                    if (item?.m_customData == null) continue;

                    string prefabName = item.m_dropPrefab?.name ?? "null";
                    bool isWO = IsWorkOrderItem(item);

                    if (!isWO)
                    {
                        continue;
                    }

                    Plugin.Log?.LogDebug(
                        $"[ContainerScan] Found work order item: {prefabName}, " +
                        $"customData keys=[{string.Join(",", item.m_customData.Keys)}]");

                    // Check station compatibility
                    if (!item.m_customData.TryGetValue("wo_station", out var station))
                    {
                        Plugin.Log?.LogDebug(
                            $"[ContainerScan] Work order missing wo_station");
                        continue;
                    }
                    if (!StationMatcher.CanWorkStation(npcType, station))
                    {
                        Plugin.Log?.LogDebug(
                            $"[ContainerScan] Station '{station}' not compatible " +
                            $"with NPC type {npcType}");
                        continue;
                    }

                    // Must have an item target
                    if (!item.m_customData.TryGetValue("wo_item", out var itemPrefab))
                    {
                        Plugin.Log?.LogDebug(
                            $"[ContainerScan] Work order missing wo_item field " +
                            $"(was this created before the update?)");
                        continue;
                    }
                    if (string.IsNullOrEmpty(itemPrefab))
                    {
                        Plugin.Log?.LogDebug(
                            $"[ContainerScan] Work order has empty wo_item");
                        continue;
                    }

                    int min = 1, max = 10;
                    if (item.m_customData.TryGetValue("wo_min", out var minStr))
                        int.TryParse(minStr, out min);
                    if (item.m_customData.TryGetValue("wo_max", out var maxStr))
                        int.TryParse(maxStr, out max);

                    Plugin.Log?.LogDebug(
                        $"[ContainerScan] Work order matched! " +
                        $"item={itemPrefab}, station={station}, qty={min}-{max}");

                    matches.Add(new WorkOrderMatch
                    {
                        SourceContainer = container,
                        ItemData = item,
                        ItemPrefabName = itemPrefab,
                        StationName = station,
                        MinQuantity = min,
                        MaxQuantity = max
                    });
                }
            }

            return matches;
        }

        /// <summary>
        /// Check if required ingredients for a recipe exist across the given containers.
        /// Returns a list describing where each ingredient can be found, or null if missing.
        /// </summary>
        public static List<IngredientSource> FindIngredients(
            List<Container> containers, Recipe recipe)
        {
            if (recipe?.m_resources == null) return null;

            var sources = new List<IngredientSource>();

            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null) continue;

                string prefabName = req.m_resItem.gameObject.name;
                int needed = req.m_amount;
                int found = 0;
                Container sourceContainer = null;

                Plugin.Log?.LogDebug(
                    $"[IngredientScan] Looking for {needed}x '{prefabName}' " +
                    $"across {containers.Count} containers");

                foreach (var container in containers)
                {
                    var inv = container.GetInventory();
                    if (inv == null) continue;

                    int count = CountByPrefab(inv, prefabName);
                    Plugin.Log?.LogDebug(
                        $"[IngredientScan] Container '{container.m_name}': " +
                        $"{count}x '{prefabName}'");

                    if (count > 0)
                    {
                        sourceContainer = container;
                        found += count;
                        if (found >= needed) break;
                    }
                }

                if (found < needed)
                {
                    Plugin.Log?.LogDebug(
                        $"[IngredientScan] MISSING: need {needed}x '{prefabName}', " +
                        $"found {found}");
                    return null;
                }

                sources.Add(new IngredientSource
                {
                    PrefabName = prefabName,
                    Amount = needed,
                    Container = sourceContainer
                });
            }

            return sources;
        }

        /// <summary>
        /// Remove ingredients from their source containers.
        /// Matches by prefab name (m_dropPrefab.name), not m_shared.m_name.
        /// </summary>
        public static bool RemoveIngredients(List<IngredientSource> sources)
        {
            foreach (var source in sources)
            {
                var inv = source.Container?.GetInventory();
                if (inv == null) return false;

                RemoveByPrefab(inv, source.PrefabName, source.Amount);
            }
            return true;
        }

        /// <summary>
        /// Check if a container can accept the crafted output item.
        /// </summary>
        public static bool CanAcceptItem(Container container, string prefabName, int amount)
        {
            if (container == null) return false;

            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null) return false;

            var inv = container.GetInventory();
            return inv != null && inv.CanAddItem(prefab, amount);
        }

        /// <summary>
        /// Deposit a crafted item into a container.
        /// Returns true on success, false if the container is full.
        /// </summary>
        public static bool TryDepositItem(Container container, string prefabName, int amount)
        {
            if (!CanAcceptItem(container, prefabName, amount)) return false;

            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null) return false;

            var inv = container.GetInventory();
            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (inv == null || itemDrop == null) return false;

            var newItem = itemDrop.m_itemData.Clone();
            newItem.m_stack = amount;
            newItem.m_dropPrefab = prefab;

            return inv.AddItem(newItem);
        }

        private static bool IsWorkOrderItem(ItemDrop.ItemData item)
        {
            string prefabName = item?.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(prefabName)) return false;

            var def = ItemFactory.GetDefinition(prefabName);
            return def != null && def.itemType == "workorder";
        }

        /// <summary>
        /// Count items in an inventory by prefab name (m_dropPrefab.name).
        /// Valheim's built-in CountItems matches m_shared.m_name which differs.
        /// </summary>
        public static int CountByPrefab(Inventory inv, string prefabName)
        {
            int total = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_dropPrefab != null && item.m_dropPrefab.name == prefabName)
                    total += item.m_stack;
            }
            return total;
        }

        /// <summary>
        /// Count total items across multiple containers by prefab name.
        /// Sums all stack sizes, not just the number of stacks.
        /// </summary>
        public static int CountAcrossContainers(
            List<Container> containers, string prefabName)
        {
            int total = 0;
            foreach (var container in containers)
            {
                var inv = container?.GetInventory();
                if (inv == null) continue;
                total += CountByPrefab(inv, prefabName);
            }
            return total;
        }

        /// <summary>
        /// Remove a quantity of items from an inventory by prefab name.
        /// Uses RemoveItem(ItemData, int) which handles partial stacks
        /// and calls Changed() internally.
        /// </summary>
        private static void RemoveByPrefab(Inventory inv, string prefabName, int amount)
        {
            int remaining = amount;
            var items = inv.GetAllItems();
            for (int i = items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var item = items[i];
                if (item?.m_dropPrefab == null || item.m_dropPrefab.name != prefabName)
                    continue;

                int toRemove = Mathf.Min(item.m_stack, remaining);
                inv.RemoveItem(item, toRemove);
                remaining -= toRemove;
            }
        }
    }

}
