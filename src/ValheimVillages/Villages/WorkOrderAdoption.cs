using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Makes each village's order set follow the work-order TOKENS physically inside it.
    ///     <para>
    ///     A token carries only its identity — <c>wo_station</c> and <c>wo_item</c> — while
    ///     the quota lives on the village ZDO. So a token is never broken by its village
    ///     going away, and carrying one into another village's chest is a request for that
    ///     village to take the order over.
    ///     </para>
    ///     <para>
    ///     Claim scope is the village FOOTPRINT — the same scope villagers use to decide what
    ///     work exists, so an adopted order is one they can actually reach.
    ///     </para>
    ///     <para>
    ///     A VILLAGE ONLY EVER WRITES ITS OWN ORDERS. It adopts tokens inside its footprint
    ///     and releases orders it has no token for; it never reaches into another village.
    ///     That is what makes a move work without any village knowing about the move: the
    ///     destination adopts because the token is there, the origin releases because it
    ///     isn't. Two earlier attempts did the transfer explicitly — one village removing
    ///     another's order — and both thrashed, because villages reconcile one after another
    ///     and each pass acted on what the previous had just changed:
    ///     <list type="number">
    ///     <item>Matching on (station, item) alone: two settlements legitimately making the
    ///     same item each stole the other's order, flipping ownership every partition.</item>
    ///     <item>Adding "…and the other village has no token of its own": whichever village
    ///     reconciled FIRST took the order, so the second found itself holding a token for an
    ///     order it no longer owned and re-adopted it at the DEFAULT quota, destroying
    ///     configured values.</item>
    ///     </list>
    ///     <para>
    ///     Releases run before adopts across all villages, and a released quota is remembered
    ///     in <see cref="s_releasedQuotas" />, so a token moved between villages keeps the
    ///     number the player chose no matter which village is processed first.
    ///     </para>
    ///     <para>
    ///     A release needs POSITIVE evidence the token is gone, never merely absent: the
    ///     village's zone must be loaded (an unloaded chest reads as "no token" and would
    ///     delete a live order), and no player may be carrying the token — an order is
    ///     created at the station with the token still in hand, so "in a pocket" is a normal
    ///     in-transit state, not an order that has ceased to exist.
    ///     </para>
    /// </summary>
    internal static class WorkOrderAdoption
    {
        /// <summary>
        ///     Quotas of orders released this session, keyed by order identity, so a token
        ///     carried between villages keeps the number the player chose. A cache, never an
        ///     authority: a miss falls back to the stack-size default.
        /// </summary>
        private static readonly Dictionary<string, WorkOrderEntry> s_releasedQuotas = new();

        [Attributes.RegisterCleanup]
        public static void Reset()
        {
            s_releasedQuotas.Clear();
        }

        /// <summary>
        ///     Reconcile every village's orders against the work-order tokens physically
        ///     inside it. Host-only. Returns how many orders changed hands.
        /// </summary>
        internal static int ReconcileAll()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return 0;

            // Snapshot each village's tokens once, before anything is written, so both
            // phases see the same world.
            var byVillage = new List<(Village village, Dictionary<string, TokenSighting> tokens)>();
            foreach (var village in VillageRegistry.EnumerateAll())
            {
                if (village == null) continue;
                var tokens = TokensIn(village);
                if (tokens == null) continue; // no footprint / zone not loaded — cannot judge
                byVillage.Add((village, tokens));
            }

            var carried = CarriedByPlayers();
            var changed = 0;

            // Phase 1 — releases, recording quotas. Before adopts so a move keeps its quota
            // regardless of which village is processed first.
            foreach (var (village, tokens) in byVillage)
                changed += Release(village, tokens, carried);

            // Phase 2 — adopts.
            foreach (var (village, tokens) in byVillage)
                changed += Adopt(village, tokens);

            return changed;
        }

        /// <summary>Drop orders this village holds no token for. Writes only this village.</summary>
        private static int Release(
            Village village, Dictionary<string, TokenSighting> tokens, HashSet<string> carried)
        {
            var doomed = new List<WorkOrderEntry>();
            foreach (var order in village.WorkOrders)
            {
                var key = Key(order.Station, order.Item);
                if (tokens.ContainsKey(key)) continue;
                if (carried.Contains(key)) continue; // in a pocket: in transit, not gone
                doomed.Add(order);
            }

            foreach (var order in doomed)
            {
                s_releasedQuotas[Key(order.Station, order.Item)] = order;
                village.RemoveWorkOrder(order.Station, order.Item);
                Plugin.Log?.LogInfo(
                    $"[WorkOrderAdoption] {village.VillageId} released {order.Item}@{order.Station} " +
                    $"[{order.Min}-{order.Max}]: no token for it in its chests");
            }

            return doomed.Count;
        }

        /// <summary>Take on tokens inside this village that it has no order for.</summary>
        private static int Adopt(Village village, Dictionary<string, TokenSighting> tokens)
        {
            var known = new HashSet<string>();
            foreach (var order in village.WorkOrders)
                known.Add(Key(order.Station, order.Item));

            var adopted = 0;
            foreach (var kv in tokens)
            {
                if (!known.Add(kv.Key)) continue;
                var token = kv.Value;

                int min, max;
                var display = token.Display;
                var restored = s_releasedQuotas.TryGetValue(kv.Key, out var previous);
                if (restored)
                {
                    min = previous.Min;
                    max = previous.Max;
                    if (string.IsNullOrEmpty(display)) display = previous.ItemDisplay;
                }
                else if (!TryDefaultQuota(token.Item, out min, out max))
                {
                    // The token names something this install cannot make. Say so — silently
                    // skipping would look like the order had never existed.
                    Plugin.Log?.LogWarning(
                        $"[WorkOrderAdoption] {village.VillageId} found a token for " +
                        $"'{token.Item}' but no such item exists here; leaving it unclaimed.");
                    known.Remove(kv.Key);
                    continue;
                }

                village.UpsertWorkOrder(
                    new WorkOrderEntry(token.Station, token.Item, display ?? "", min, max));
                adopted++;
                Plugin.Log?.LogInfo(
                    $"[WorkOrderAdoption] {village.VillageId} adopted {token.Item}@{token.Station} " +
                    $"[{min}-{max}]{(restored ? " (quota carried over)" : "")} from a chest at " +
                    $"({token.Where.x:F0},{token.Where.z:F0})");
            }

            return adopted;
        }

        /// <summary>
        ///     Order identities currently in a player's inventory. An order is created at the
        ///     crafting station while its token is still in hand, so without this the very
        ///     next reconcile would delete a just-made order before it reaches a chest.
        /// </summary>
        private static HashSet<string> CarriedByPlayers()
        {
            var carried = new HashSet<string>();
            var players = Player.GetAllPlayers();
            if (players == null) return carried;

            foreach (var player in players)
            {
                var inv = player != null ? player.GetInventory() : null;
                if (inv == null) continue;
                foreach (var item in inv.GetAllItems())
                {
                    if (!ContainerScanner.IsWorkOrderItem(item) || item.m_customData == null)
                        continue;
                    item.m_customData.TryGetValue("wo_item", out var orderItem);
                    if (string.IsNullOrEmpty(orderItem)) continue;
                    item.m_customData.TryGetValue("wo_station", out var station);
                    carried.Add(Key(station ?? "", orderItem));
                }
            }

            return carried;
        }

        private readonly struct TokenSighting
        {
            public readonly string Station;
            public readonly string Item;
            public readonly string Display;
            public readonly Vector3 Where;

            public TokenSighting(string station, string item, string display, Vector3 where)
            {
                Station = station;
                Item = item;
                Display = display;
                Where = where;
            }
        }

        /// <summary>
        ///     Work-order tokens in chests inside this village's footprint, keyed by
        ///     order identity. Null when the village has no footprint yet — which is "we
        ///     cannot tell", not "there are none", and must never drive a release.
        /// </summary>
        private static Dictionary<string, TokenSighting> TokensIn(Village village)
        {
            if (!village.TryGetFootprint(out var minX, out var minZ, out var maxX, out var maxZ))
                return null;

            // An unloaded village has no instantiated chests, so a scan would report "no
            // tokens" and the release phase would wipe every order it has.
            var anchor = village.Anchor;
            if (anchor == Vector3.zero) return null;
            if (ZoneSystem.instance == null
                || !ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(anchor)))
                return null;

            var found = new Dictionary<string, TokenSighting>();
            foreach (var container in ContainerScanner.FindVillageContainers(
                         village.Anchor, WorkSettings.ChestScanRadius))
            {
                if (container == null) continue;
                var pos = container.transform.position;
                if (pos.x < minX || pos.x > maxX || pos.z < minZ || pos.z > maxZ) continue;

                var inv = container.GetInventory();
                if (inv == null) continue;

                foreach (var item in inv.GetAllItems())
                {
                    if (!ContainerScanner.IsWorkOrderItem(item) || item.m_customData == null)
                        continue;

                    item.m_customData.TryGetValue("wo_item", out var orderItem);
                    if (string.IsNullOrEmpty(orderItem)) continue;
                    item.m_customData.TryGetValue("wo_station", out var station);
                    item.m_customData.TryGetValue("wo_item_name", out var display);

                    var key = Key(station ?? "", orderItem);
                    if (!found.ContainsKey(key))
                        found[key] = new TokenSighting(station ?? "", orderItem, display ?? "", pos);
                }
            }

            return found;
        }

        /// <summary>
        ///     The quota another village already uses for this order, if any — so a token
        ///     carried between villages keeps the number the player chose instead of being
        ///     reset to the stack default. Strictly READ ONLY.
        /// </summary>
        private static WorkOrderEntry FindQuotaElsewhere(
            string excludeVillageId, TokenSighting token, out bool found)
        {
            foreach (var other in VillageRegistry.EnumerateAll())
            {
                if (other == null || other.VillageId == excludeVillageId) continue;
                foreach (var order in other.WorkOrders)
                {
                    if (order.Station != token.Station || order.Item != token.Item) continue;
                    found = true;
                    return order;
                }
            }

            found = false;
            return default;
        }

        private static string Key(string station, string item)
        {
            return $"{item}@{station}";
        }

        /// <summary>
        ///     Quota for a newly adopted order: one full stack, refilling at half a stack —
        ///     the same derivation a freshly placed order uses.
        /// </summary>
        private static bool TryDefaultQuota(string itemPrefabName, out int min, out int max)
        {
            min = 0;
            max = 0;
            var prefab = ObjectDB.instance != null
                ? ObjectDB.instance.GetItemPrefab(itemPrefabName)
                : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            var shared = drop != null ? drop.m_itemData?.m_shared : null;
            if (shared == null || shared.m_maxStackSize < 1) return false;

            max = shared.m_maxStackSize;
            min = (max + 1) / 2;
            return true;
        }
    }
}
