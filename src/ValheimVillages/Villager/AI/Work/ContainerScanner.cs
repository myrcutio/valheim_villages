using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Items;
using ValheimVillages.Schemas;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Utility for scanning nearby containers for work orders and ingredients.
    /// </summary>
    public static class ContainerScanner
    {
        /// <summary>
        ///     Find all live Container network objects within the given radius of a position.
        ///     <para>The source of truth is <c>UnityEngine.Object.FindObjectsOfType&lt;Container&gt;()</c>
        ///     — the same authoritative enumeration of instantiated, ZNetView-backed network objects
        ///     that the <c>printnetobj</c> console command uses — NOT a <see cref="PhysicsHelper" />
        ///     <c>OverlapSphere</c> collider query. OverlapSphere is unreliable on a headless dedicated
        ///     server (its physics/collider state diverges from clients), which made the server
        ///     enumerate phantom chests and read stale work orders while the client saw the real set.
        ///     Results are filtered to objects holding a valid ZDO and deduped by ZDO id, so a single
        ///     networked chest is counted exactly once on every peer.</para>
        /// </summary>
        public static List<Container> FindNearbyContainers(Vector3 center, float radius)
        {
            var result = new List<Container>();
            var seen = new HashSet<ZDOID>();
            var sqrRadius = radius * radius;

            foreach (var container in UnityEngine.Object.FindObjectsOfType<Container>())
            {
                if (container == null) continue;

                var nview = container.GetComponent<ZNetView>();
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null) continue; // skip objects without a live networked ZDO

                if ((container.transform.position - center).sqrMagnitude > sqrRadius) continue;
                if (!seen.Add(zdo.m_uid)) continue; // collapse duplicate instances sharing one ZDO

                result.Add(container);
            }

            return result;
        }

        /// <summary>
        ///     All work-order configs for <paramref name="village" /> that
        ///     <paramref name="villagerType" /> can work. Reads the host-owned village record
        ///     (Fix C) — config no longer lives on chest tokens, so it can't be clobbered by the
        ///     chest's ownership churn. The returned <see cref="WorkOrderMatch" /> carries no
        ///     ItemData/SourceContainer; the scan handler picks a deterministic deposit chest by
        ///     proximity. Completion is still measured by scanning chests (see CountAcrossContainers).
        /// </summary>
        public static List<WorkOrderMatch> FindAllWorkOrders(Village village, string villagerType)
        {
            var matches = new List<WorkOrderMatch>();
            if (village == null) return matches;

            foreach (var entry in village.WorkOrders)
            {
                if (string.IsNullOrEmpty(entry.Station)) continue;
                if (!StationMatcher.CanWorkStation(villagerType, entry.Station)) continue;
                if (string.IsNullOrEmpty(entry.Item)) continue;

                matches.Add(new WorkOrderMatch
                {
                    ItemData = null, // config no longer rides a chest token
                    SourceContainer = null, // deposit chest is chosen by the scan from proximity
                    ItemPrefabName = entry.Item,
                    StationName = entry.Station,
                    MinQuantity = entry.Min,
                    MaxQuantity = entry.Max,
                });
            }

            return matches;
        }

        /// <summary>
        ///     The authoritative Max quota for a (station, item) order from the host-owned village
        ///     record near <paramref name="pos" /> (Fix C). Falls back to <paramref name="tokenMax" />
        ///     (the legacy chest-token value) only when no village/record entry resolves — e.g. an
        ///     un-migrated or orphaned token.
        /// </summary>
        public static int ResolveOrderMax(string station, string itemPrefab, Vector3 pos, int tokenMax)
        {
            var village = VillageRegistry.GetVillageCovering(pos) ?? VillageRegistry.FindNearAnchor(pos);
            if (village != null && !string.IsNullOrEmpty(station) && !string.IsNullOrEmpty(itemPrefab)
                && village.TryGetWorkOrder(station, itemPrefab, out var entry))
                return entry.Max;
            return tokenMax;
        }

        /// <summary>
        ///     Nearest container to a point — the deterministic deposit/scan chest now that the
        ///     work-order token no longer carries its source chest. Null only if the list is empty.
        /// </summary>
        public static Container FindNearestContainer(List<Container> containers, Vector3 pos)
        {
            Container best = null;
            var bestSq = float.MaxValue;
            foreach (var c in containers)
            {
                if (c == null) continue;
                var d = (c.transform.position - pos).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        ///     Check if required ingredients for a recipe exist across the given containers.
        ///     Returns a list describing where each ingredient can be found, or null if missing.
        /// </summary>
        public static List<IngredientSource> FindIngredients(
            List<Container> containers, Recipe recipe)
        {
            if (recipe?.m_resources == null) return null;

            var sources = new List<IngredientSource>();

            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null) continue;

                var prefabName = req.m_resItem.gameObject.name;
                var needed = req.m_amount;
                var found = 0;
                Container sourceContainer = null;

                Plugin.Log?.LogDebug(
                    $"[IngredientScan] Looking for {needed}x '{prefabName}' " +
                    $"across {containers.Count} containers");

                foreach (var container in containers)
                {
                    var inv = container.GetInventory();
                    if (inv == null) continue;

                    var count = CountByPrefab(inv, prefabName);
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
                    Container = sourceContainer,
                });
            }

            return sources;
        }

        /// <summary>
        ///     Ensure the local peer owns the container's ZDO before a villager mutates
        ///     it. Villagers run on the server; a chest the player has open is
        ///     client-owned, and <see cref="Container" />.Save() is owner-gated — so
        ///     without this the villager's add/remove would touch only the server's local
        ///     copy, get reverted on the next ZDO sync, and never show up for the player.
        ///     Claiming (a no-op when already owner) makes the villager act AS the owner,
        ///     so the change persists to the ZDO and replicates to every peer — including
        ///     a player viewing the chest live.
        /// </summary>
        public static void EnsureOwnership(Container container)
        {
            if (container == null) return;
            var nview = container.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && !nview.IsOwner())
                nview.ClaimOwnership();
        }

        /// <summary>
        ///     Remove ingredients from their source containers.
        ///     Matches by prefab name (m_dropPrefab.name), not m_shared.m_name.
        /// </summary>
        public static bool RemoveIngredients(List<IngredientSource> sources)
        {
            // Claim every source, then VERIFY all are fully stocked, THEN remove.
            // Verifying before removing makes this atomic: if any ingredient ran out
            // between the scan and the villager's arrival (player/another villager took
            // it), we remove nothing and return false so the caller aborts — instead of
            // silently removing 0 and letting the workflow fabricate a held item /
            // conjure a station input from ingredients that aren't there.
            foreach (var source in sources)
                EnsureOwnership(source.Container);

            foreach (var source in sources)
            {
                var inv = source.Container?.GetInventory();
                if (inv == null || CountByPrefab(inv, source.PrefabName) < source.Amount)
                    return false;
            }

            foreach (var source in sources)
                RemoveByPrefab(source.Container.GetInventory(), source.PrefabName, source.Amount);

            return true;
        }

        /// <summary>
        ///     Check if a container can accept the crafted output item.
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
        ///     Deposit a crafted item into a container.
        ///     Returns true on success, false if the container is full.
        /// </summary>
        public static bool TryDepositItem(Container container, string prefabName, int amount)
        {
            EnsureOwnership(container);
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

        /// <summary>
        ///     Whether a container has room for the given (actual) item stack.
        ///     Unlike <see cref="CanAcceptItem" /> this checks the real item data
        ///     (stack, quality), so it's correct for hauling picked-up drops.
        /// </summary>
        public static bool CanAcceptItemData(Container container, ItemDrop.ItemData item)
        {
            var inv = container?.GetInventory();
            return inv != null && item != null && inv.CanAddItem(item, item.m_stack);
        }

        /// <summary>
        ///     Deposit an actual item stack (preserving stack/quality/custom data)
        ///     into a container. Returns false if the container can't fit it.
        /// </summary>
        public static bool TryDepositItemData(Container container, ItemDrop.ItemData item)
        {
            EnsureOwnership(container);
            if (!CanAcceptItemData(container, item)) return false;
            return container.GetInventory().AddItem(item);
        }

        public static bool IsWorkOrderItem(ItemDrop.ItemData item)
        {
            var prefabName = item?.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(prefabName)) return false;

            var def = ItemFactory.GetDefinition(prefabName);
            return def != null && def.itemType == "workorder";
        }

        /// <summary>
        ///     Count items in an inventory by prefab name (m_dropPrefab.name).
        ///     Valheim's built-in CountItems matches m_shared.m_name which differs.
        /// </summary>
        public static int CountByPrefab(Inventory inv, string prefabName)
        {
            var total = 0;
            foreach (var item in inv.GetAllItems())
                if (item?.m_dropPrefab != null && item.m_dropPrefab.name == prefabName)
                    total += item.m_stack;
            return total;
        }

        /// <summary>
        ///     Count total items across multiple containers by prefab name.
        ///     Sums all stack sizes, not just the number of stacks.
        /// </summary>
        public static int CountAcrossContainers(
            List<Container> containers, string prefabName)
        {
            var total = 0;
            foreach (var container in containers)
            {
                var inv = container?.GetInventory();
                if (inv == null) continue;
                total += CountByPrefab(inv, prefabName);
            }

            return total;
        }

        /// <summary>
        ///     Remove a quantity of items from an inventory by prefab name.
        ///     Uses RemoveItem(ItemData, int) which handles partial stacks
        ///     and calls Changed() internally.
        /// </summary>
        private static void RemoveByPrefab(Inventory inv, string prefabName, int amount)
        {
            var remaining = amount;
            var items = inv.GetAllItems();
            for (var i = items.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var item = items[i];
                if (item?.m_dropPrefab == null || item.m_dropPrefab.name != prefabName)
                    continue;

                var toRemove = Mathf.Min(item.m_stack, remaining);
                inv.RemoveItem(item, toRemove);
                remaining -= toRemove;
            }
        }
    }
}