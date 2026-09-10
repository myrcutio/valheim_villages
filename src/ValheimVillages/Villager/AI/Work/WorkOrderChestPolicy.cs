using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Settings;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     A chest holding work-order tokens is a RESERVED chest: the player parked the order
    ///     there to say "this job lives here". Villagers must not fill it with unrelated goods,
    ///     because every slot they take is a slot the order's output cannot land in — which is
    ///     exactly the "Output chest full" rejection that stalls the order before a station is
    ///     ever picked.
    ///
    ///     <para>Related, for every token in that chest, means: the order's output item, the
    ///     ingredients of the recipe that produces it, the fuel its station burns, and work-order
    ///     tokens themselves (so a stray token can still be filed). A chest holding no token is
    ///     unreserved and takes anything, as before.</para>
    ///
    ///     <para>This governs where villagers CHOOSE to put things. It deliberately does not gate
    ///     <see cref="ContainerScanner" />'s deposit primitives: rolling a withdrawn ingredient
    ///     back to the chest it came from is undoing a withdrawal, not storing junk, and must
    ///     never be refused.</para>
    /// </summary>
    public static class WorkOrderChestPolicy
    {
        /// <summary>
        ///     How long a resolved allow-list stays good. The token set is re-read every call (a
        ///     cheap inventory walk); this only bounds how stale the derived half — recipe
        ///     ingredients and the station's fuel item, which need ObjectDB and world lookups —
        ///     is allowed to get.
        /// </summary>
        private const float CacheSeconds = 5f;

        private static readonly Dictionary<ZDOID, Entry> s_cache = new();

        /// <summary>True when this chest holds at least one work-order token.</summary>
        public static bool IsReserved(Container container)
        {
            return TryGetAllowed(container, out _);
        }

        /// <summary>
        ///     Whether a villager may store <paramref name="prefabName" /> in this chest.
        ///     Unreserved chests accept anything.
        /// </summary>
        public static bool Allows(Container container, string prefabName)
        {
            if (!TryGetAllowed(container, out var allowed)) return true;
            if (string.IsNullOrEmpty(prefabName)) return false;
            return allowed.Contains(prefabName) || ContainerScanner.IsWorkOrderPrefab(prefabName);
        }

        /// <summary>Item-data overload, for hauling a picked-up ground drop.</summary>
        public static bool Allows(Container container, ItemDrop.ItemData item)
        {
            return Allows(container, item?.m_dropPrefab?.name);
        }

        /// <summary>
        ///     The chest's reservation as a one-line summary, for <c>vv_chestpolicy</c>. Null when
        ///     the chest holds no work order.
        /// </summary>
        public static string Describe(Container container)
        {
            if (!TryGetAllowed(container, out var allowed)) return null;

            var names = new List<string>(allowed);
            names.Sort();
            return names.Count > 0 ? string.Join(", ", names) : "(nothing — orders unresolvable)";
        }

        /// <summary>
        ///     Resolve the chest's allow-list, or false when it holds no work order.
        /// </summary>
        private static bool TryGetAllowed(Container container, out HashSet<string> allowed)
        {
            allowed = null;

            var inv = container?.GetInventory();
            if (inv == null) return false;

            var nview = container.GetComponent<ZNetView>();
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo == null) return false;

            var orders = ReadOrders(inv);
            if (orders == null) return false;

            // Signature keys the cache on WHICH orders are parked here, so pulling a token out or
            // dropping a new one in takes effect on the next query rather than after the TTL.
            var signature = Signature(orders);
            if (s_cache.TryGetValue(zdo.m_uid, out var entry)
                && entry.Signature == signature
                && Time.time < entry.Expiry)
            {
                allowed = entry.Allowed;
                return true;
            }

            allowed = BuildAllowed(orders, container.transform.position);
            s_cache[zdo.m_uid] = new Entry(signature, allowed, Time.time + CacheSeconds);
            return true;
        }

        /// <summary>The (item, station) pairs of every work-order token in the inventory.</summary>
        private static List<Order> ReadOrders(Inventory inv)
        {
            List<Order> orders = null;

            foreach (var item in inv.GetAllItems())
            {
                if (!ContainerScanner.IsWorkOrderItem(item)) continue;
                if (item.m_customData == null) continue;

                item.m_customData.TryGetValue("wo_item", out var orderItem);
                if (string.IsNullOrEmpty(orderItem)) continue;

                item.m_customData.TryGetValue("wo_station", out var station);
                orders ??= new List<Order>();
                orders.Add(new Order(orderItem, station ?? ""));
            }

            return orders;
        }

        private static string Signature(List<Order> orders)
        {
            var keys = new List<string>(orders.Count);
            foreach (var o in orders) keys.Add($"{o.Item}@{o.Station}");
            keys.Sort();
            return string.Join(",", keys);
        }

        private static HashSet<string> BuildAllowed(List<Order> orders, Vector3 chestPos)
        {
            var allowed = new HashSet<string>();

            foreach (var order in orders)
            {
                allowed.Add(order.Item);

                var recipe = StationMatcher.FindRecipeForOrder(order.Item, order.Station);
                if (recipe == null) continue;

                if (recipe.m_resources != null)
                    foreach (var req in recipe.m_resources)
                        Add(allowed, req.m_resItem);

                AddStationFuel(allowed, recipe, chestPos);
            }

            return allowed;
        }

        /// <summary>
        ///     The fuel the order's station burns. A cooking order that cannot keep its fire fed
        ///     never finishes, so the fuel is as much part of the order as the ingredients — but
        ///     it lives on the station, not the recipe, so it has to be resolved from the world.
        /// </summary>
        private static void AddStationFuel(HashSet<string> allowed, Recipe recipe, Vector3 chestPos)
        {
            var physical = VirtualRecipeLoader.GetPhysicalStation(recipe.name);
            if (string.IsNullOrEmpty(physical)) return;

            if (physical == "cookingstation")
            {
                var station = NearestInVillage<CookingStation>(chestPos);
                if (station == null) return;

                if (station.m_requireFire
                    && StationFuelHelper.TryFindFireplaceNear(station, out var fireplace))
                    Add(allowed, fireplace.m_fuelItem);
                else if (station.m_useFuel)
                    Add(allowed, station.m_fuelItem);

                return;
            }

            // Smelter-family fuel is on the prefab, so no world lookup is needed. A smelter with
            // no m_fuelItem (kiln, windmill, spinning wheel) burns its input and adds nothing.
            var smelterPrefab = StationFinder.GetSmelterPrefab(physical);
            if (smelterPrefab != null) Add(allowed, smelterPrefab.m_fuelItem);
        }

        private static void Add(HashSet<string> allowed, ItemDrop item)
        {
            if (item != null) allowed.Add(item.gameObject.name);
        }

        /// <summary>
        ///     Nearest instantiated component of type T within chest-scan range. Enumerates live
        ///     objects rather than using an OverlapSphere for the same reason
        ///     <see cref="ContainerScanner.FindNearbyContainers" /> does: collider state diverges
        ///     on a headless server, and villagers run on the host.
        /// </summary>
        private static T NearestInVillage<T>(Vector3 pos) where T : Component
        {
            T best = null;
            var bestSq = WorkSettings.ChestScanRadius * WorkSettings.ChestScanRadius;

            foreach (var candidate in Object.FindObjectsByType<T>(FindObjectsSortMode.None))
            {
                if (candidate == null) continue;
                var d = (candidate.transform.position - pos).sqrMagnitude;
                if (d > bestSq) continue;
                bestSq = d;
                best = candidate;
            }

            return best;
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_cache.Clear();
        }

        private readonly struct Order
        {
            public readonly string Item;
            public readonly string Station;

            public Order(string item, string station)
            {
                Item = item;
                Station = station;
            }
        }

        private readonly struct Entry
        {
            public readonly string Signature;
            public readonly HashSet<string> Allowed;
            public readonly float Expiry;

            public Entry(string signature, HashSet<string> allowed, float expiry)
            {
                Signature = signature;
                Allowed = allowed;
                Expiry = expiry;
            }
        }
    }
}
