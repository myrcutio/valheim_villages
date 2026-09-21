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
        /// <summary>
        ///     Every container belonging to the village that contains <paramref name="anchorPos" />,
        ///     using the village's published XZ footprint rather than a radius around the anchor.
        ///     <para>
        ///         A radius is centred on whichever villager is asking, so half a real settlement
        ///         falls outside it — a Farmer's ingredient chest measured 23.5m from its anchor
        ///         against a 20m radius, so the scan reported "missing ingredients" while the
        ///         deer meat sat in a chest reserved for exactly that order.
        ///     </para>
        ///     Falls back to <paramref name="fallbackRadius" /> when no village resolves or it has
        ///     no footprint yet (no partition since world load) — never silently returns nothing.
        /// </summary>
        public static List<Container> FindVillageContainers(Vector3 anchorPos, float fallbackRadius)
        {
            var village = Villages.Entity.VillageRegistry.GetVillageAt(anchorPos)
                          ?? Villages.Entity.VillageRegistry.FindNearAnchor(anchorPos);

            if (village == null || !village.TryGetFootprint(
                    out var minX, out var minZ, out var maxX, out var maxZ))
                return FindNearbyContainers(anchorPos, fallbackRadius);

            var result = new List<Container>();
            var seen = new HashSet<ZDOID>();
            foreach (var container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                if (container == null) continue;

                var nview = container.GetComponent<ZNetView>();
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null) continue;

                var p = container.transform.position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
                if (!seen.Add(zdo.m_uid)) continue;

                result.Add(container);
            }

            return result;
        }

        /// <summary>
        ///     The subset of <paramref name="containers" /> the villager at
        ///     <paramref name="pathSource" /> can actually walk to.
        ///     <para>
        ///         Village scope and reachability are two different questions and they disagree:
        ///         the footprint that scopes a village is grown to enclose every player-built
        ///         piece within 100m, while reachability comes from the region graph, so a chest
        ///         is routinely in scope and provably unwalkable. Anything that decides "can this
        ///         villager do this order?" must ask both — the work-order scan AND the scheduler
        ///         producer that feeds it, or the board offers a row the scan refuses and the
        ///         villager churns dispatch -> "no work payload" -> abandon forever.
        ///     </para>
        ///     <para>
        ///         Deliberately NOT applied to quota counting: "does the village hold enough?"
        ///         is a question about the village, not about one villager's legs.
        ///     </para>
        /// </summary>
        public static List<Container> FilterReachable(List<Container> containers, Vector3 pathSource)
        {
            var result = new List<Container>(containers?.Count ?? 0);
            if (containers == null) return result;

            foreach (var container in containers)
                if (container != null &&
                    Navigation.VillagerMovement.TryResolveApproach(
                        container.transform.position, pathSource, null, out _))
                    result.Add(container);

            return result;
        }

        public static List<Container> FindNearbyContainers(Vector3 center, float radius)
        {
            var result = new List<Container>();
            var seen = new HashSet<ZDOID>();
            var sqrRadius = radius * radius;

            foreach (var container in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
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
        ///     The authoritative Max quota for a (station, item) order, from the host-owned
        ///     village record near <paramref name="pos" />. Returns false when no village or
        ///     record entry resolves.
        ///     <para>
        ///         This used to fall back to the token's legacy <c>wo_max</c>, which defaulted to
        ///         10 — that fallback is precisely what made a failure to resolve the village
        ///         present as a work order silently stuck at a 1-10 range instead of reporting
        ///         that it could not read the record. The legacy in-chest token format is no
        ///         longer supported (it was last written before 0.2, and the migration command
        ///         that repaired it is gone), so an unresolved order is now a real error and
        ///         callers must surface it rather than invent a quota.
        ///     </para>
        /// </summary>
        public static bool TryResolveOrderMax(string station, string itemPrefab, Vector3 pos, out int max)
        {
            max = 0;
            var village = VillageRegistry.GetVillageCovering(pos) ?? VillageRegistry.FindNearAnchor(pos);
            if (village == null || string.IsNullOrEmpty(station) || string.IsNullOrEmpty(itemPrefab)
                || !village.TryGetWorkOrder(station, itemPrefab, out var entry))
                return false;

            max = entry.Max;
            return true;
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
        ///     The first ingredient of <paramref name="recipe" /> the villager cannot cover, and
        ///     — the part that decides what the player should DO about it — whether the village
        ///     HAS the thing in a chest nobody can walk to.
        ///
        ///     <para>Separate from <see cref="FindIngredients" /> because that one answers
        ///     "can I start?" and throws away WHICH requirement failed.</para>
        /// </summary>
        /// <param name="reachable">Chests the villager can actually walk to.</param>
        /// <param name="inVillage">
        ///     Every chest in the village footprint. Pass null to skip the out-of-reach check.
        /// </param>
        public static bool TryFindShortfall(
            List<Container> reachable, List<Container> inVillage, Recipe recipe,
            out IngredientShortfall shortfall)
        {
            shortfall = default;
            if (recipe?.m_resources == null) return false;

            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null) continue;

                var prefabName = req.m_resItem.gameObject.name;
                var have = CountAcrossContainers(reachable, prefabName);
                if (have >= req.m_amount) continue;

                var token = req.m_resItem.m_itemData?.m_shared?.m_name;
                shortfall = new IngredientShortfall
                {
                    PrefabName = prefabName,
                    DisplayName = string.IsNullOrEmpty(token)
                        ? prefabName
                        : Localization.instance.Localize(token),
                    Needed = req.m_amount,
                    FoundReachable = have,
                    FoundInVillage = inVillage != null
                        ? CountAcrossContainers(inVillage, prefabName)
                        : have,
                };

                if (shortfall.FoundInVillage >= req.m_amount)
                    shortfall.OutOfReachHolder = FindUnreachableHolder(inVillage, reachable, prefabName);

                return true;
            }

            return false;
        }

        /// <summary>
        ///     The chest the player should move: one holding the item that the villager has no
        ///     approach to. Falls back to any holder, so the report always names a place.
        /// </summary>
        private static Container FindUnreachableHolder(
            List<Container> inVillage, List<Container> reachable, string prefabName)
        {
            if (inVillage == null) return null;

            Container anyHolder = null;
            foreach (var container in inVillage)
            {
                var inv = container?.GetInventory();
                if (inv == null || CountByPrefab(inv, prefabName) <= 0) continue;

                anyHolder ??= container;
                if (reachable == null || !reachable.Contains(container)) return container;
            }

            return anyHolder;
        }

        /// <summary>
        ///     Where to collect each of a recipe's ingredients, or null when the containers
        ///     cannot cover it.
        ///
        ///     <para><b>One entry per CHEST, not per ingredient.</b> This used to total an
        ///     ingredient across every container while remembering only the last chest it saw
        ///     any in, then record that one chest as the source for the FULL amount. Whenever a
        ///     stack was split across chests the total said yes and the withdrawal said no:
        ///     <c>RemoveIngredients</c> verifies before removing (deliberately — a partial
        ///     withdrawal would let the workflow fabricate a held item), so the villager
        ///     abandoned with "ingredient X no longer available", went Idle, rescanned, matched
        ///     the same order, and did it again. Measured on a live server: a Farmer looping on
        ///     TurnipStew several times a second, seven log lines an iteration, having never
        ///     picked up a single turnip.</para>
        ///
        ///     <para>The gathering workflows walk this list one chest at a time
        ///     (<c>CurrentIngredientIndex</c>), so several entries for one ingredient simply
        ///     means several stops.</para>
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
                var remaining = needed;
                var picks = new List<IngredientSource>();

                if (Settings.LogSettings.VerboseIngredientScan)
                    Plugin.Log?.LogDebug(
                        $"[IngredientScan] Looking for {needed}x '{prefabName}' " +
                        $"across {containers.Count} containers");

                foreach (var container in containers)
                {
                    var inv = container.GetInventory();
                    if (inv == null) continue;

                    var count = CountByPrefab(inv, prefabName);
                    if (Settings.LogSettings.VerboseIngredientScan)
                        Plugin.Log?.LogDebug(
                            $"[IngredientScan] Container '{container.m_name}': " +
                            $"{count}x '{prefabName}'");

                    if (count <= 0) continue;

                    // Take only what this chest can actually give, so the recorded amount is
                    // one the withdrawal can satisfy.
                    var take = Mathf.Min(count, remaining);
                    picks.Add(new IngredientSource
                    {
                        PrefabName = prefabName,
                        Amount = take,
                        Container = container,
                    });

                    remaining -= take;
                    if (remaining <= 0) break;
                }

                if (remaining > 0)
                {
                    LogMissingIngredient(prefabName, needed, needed - remaining, containers.Count);
                    return null;
                }

                sources.AddRange(picks);
            }

            return sources;
        }

        /// <summary>
        ///     Convenience passthrough to <see cref="WorkOrderCooldown.IsCoolingDown" />, so the
        ///     scan asks one type about work-order availability rather than two.
        /// </summary>
        public static bool IsOnCooldown(
            string villagerId, string itemPrefab, out string reason, out float secondsLeft)
        {
            return WorkOrderCooldown.IsCoolingDown(villagerId, itemPrefab, out reason, out secondsLeft);
        }

        /// <summary>Seconds before an UNCHANGED missing-ingredient report repeats.</summary>
        private const float MissingHeartbeatSeconds = 30f;

        /// <summary>
        ///     Signature -> <c>Time.time</c> it was last reported.
        /// </summary>
        private static readonly Dictionary<string, float> s_lastMissingLog = new();

        /// <summary>
        ///     Report a shortfall, but only when the shortfall CHANGES (or as an occasional
        ///     heartbeat). A crafter that is short an ingredient re-scans continuously and
        ///     re-reported an identical line every time — 290 copies of "MISSING: need 5x
        ///     'Raspberry', found 4" in a single session. The state change is the event
        ///     worth a line; the steady state is not, and drowning the ring buffer in it
        ///     hides the diagnostics someone is actually reading.
        /// </summary>
        private static void LogMissingIngredient(string prefabName, int needed, int found, int containerCount)
        {
            var signature = prefabName + "|" + needed + "|" + found;
            var now = Time.time;
            if (s_lastMissingLog.TryGetValue(signature, out var last)
                && now - last < MissingHeartbeatSeconds)
                return;

            s_lastMissingLog[signature] = now;
            Plugin.Log?.LogDebug(
                $"[IngredientScan] MISSING: need {needed}x '{prefabName}', " +
                $"found {found} across {containerCount} container(s)");
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
            return IsWorkOrderPrefab(item?.m_dropPrefab?.name);
        }

        /// <summary>
        ///     Same test as <see cref="IsWorkOrderItem" /> for callers that only hold a prefab
        ///     name (a ground drop's prefab, a recipe output) and not the live ItemData.
        /// </summary>
        public static bool IsWorkOrderPrefab(string prefabName)
        {
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