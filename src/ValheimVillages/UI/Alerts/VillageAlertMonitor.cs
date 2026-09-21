using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.UI.Alerts
{
    /// <summary>
    ///     Watches each village for problems only the player can fix and puts a "!" over the
    ///     villager best placed to mention it:
    ///
    ///     <list type="bullet">
    ///       <item>Storage nearly full, measured in EMPTY SLOTS across the village's chests.</item>
    ///       <item>A work order that cannot proceed because an ingredient has run out. The
    ///             shortfall is derived from the recipe the villager actually knows
    ///             (<see cref="StationMatcher.FindRecipeForNpc" />), so it names the real missing
    ///             item rather than a hardcoded guess.</item>
    ///     </list>
    ///
    ///     <para>Ingredient alerts win over the storage alert on the same villager: it is the
    ///     more specific and more actionable of the two.</para>
    /// </summary>
    public static class VillageAlertMonitor
    {
        /// <summary>Fraction of slots still free at or below which storage counts as "almost out".</summary>
        public const float StorageFreeFraction = 0.10f;

        /// <summary>Seconds between evaluations. Chest scans are not free; this is not per-frame work.</summary>
        private const float EvaluateInterval = 5f;

        private static float s_nextEvaluate;

        /// <summary>
        ///     Villager currently carrying each village's storage warning. Kept sticky: without
        ///     this the "nearest to the player" election re-runs every interval and the badge
        ///     hops between villagers as the player walks, which reads as a glitch.
        /// </summary>
        private static readonly Dictionary<string, string> s_storageSpeaker = new();

        /// <summary>
        ///     How much closer another villager must be before the storage warning moves to
        ///     them. 1 = re-elect constantly; higher = stickier.
        /// </summary>
        private const float SpeakerHandoverRatio = 1.5f;

        /// <summary>
        ///     Throttled entry point, called from <c>Plugin.Update</c>. No-ops on a dedicated
        ///     server (no local player to show anything to) and between intervals.
        /// </summary>
        public static void Tick()
        {
            if (Player.m_localPlayer == null) return;
            if (Time.time < s_nextEvaluate) return;
            s_nextEvaluate = Time.time + EvaluateInterval;

            // Villagers grouped by village, so the storage check runs once per village rather
            // than once per villager.
            var byVillage = new Dictionary<string, List<VillagerAI>>();
            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai == null) continue;
                var village = VillageRegistry.GetVillageAt(ai.HomeAnchor);
                if (village == null) continue;

                if (!byVillage.TryGetValue(village.VillageId, out var list))
                {
                    list = new List<VillagerAI>();
                    byVillage[village.VillageId] = list;
                }

                list.Add(ai);
            }

            foreach (var kv in byVillage)
                EvaluateVillage(kv.Value);
        }

        private static void EvaluateVillage(List<VillagerAI> villagers)
        {
            if (villagers.Count == 0) return;

            var village = VillageRegistry.GetVillageAt(villagers[0].HomeAnchor);
            if (village == null) return;

            // Village-wide chests for the storage figure...
            var villageContainers = ContainerScanner.FindNearbyContainers(
                village.Anchor, WorkSettings.ChestScanRadius);

            var storageTight = IsStorageTight(villageContainers, out var freeSlots, out var totalSlots);

            // The storage warning belongs to whichever villager the player is most likely to
            // walk past — nearest to the player — but only moves once someone is clearly
            // closer, so it doesn't hop about as the player wanders.
            VillagerAI storageSpeaker = storageTight
                ? ElectStorageSpeaker(village.VillageId, villagers)
                : null;
            if (!storageTight) s_storageSpeaker.Remove(village.VillageId);

            foreach (var ai in villagers)
            {
                var marker = EnsureMarker(ai);
                if (marker == null) continue;

                // ...but the shortfall is scanned from the VILLAGER's own anchor, with the same
                // radius the crafting flow uses. Scanning from the village anchor instead could
                // report a shortage the villager cannot actually see (or miss one it can), so
                // the alert would contradict the behavior it is describing.
                var mine = ContainerScanner.FindNearbyContainers(
                    ai.HomeAnchor, WorkSettings.ChestScanRadius);

                // A chest with no room to deposit into blocks the villager outright — the work
                // order scan rejects with "Output chest full" before it even looks at a station,
                // so this outranks both other alerts. Checked per-order because the villager is
                // blocked on ONE specific chest; the village-wide fraction below can sit
                // comfortably under the threshold while the chest that matters is jammed.
                if (TryDescribeFullOutputChest(village, ai, mine, out var jammed, out var jammedItem))
                {
                    marker.Set(jammed, jammedItem);
                    continue;
                }

                // Per-villager ingredient shortfall takes precedence over storage: it is specific.
                if (TryDescribeShortfall(village, ai, mine, out var shortfall, out var shortItem))
                {
                    marker.Set(shortfall, shortItem);
                    continue;
                }

                if (storageTight && ai == storageSpeaker)
                {
                    marker.Set("We're almost out of storage!");
                    continue;
                }

                marker.Clear();
            }

            if (storageTight)
                Plugin.Log?.LogDebug(
                    $"[VillageAlert] {village.VillageId}: storage tight " +
                    $"({freeSlots}/{totalSlots} slots free)");
        }

        /// <summary>
        ///     Storage pressure measured in empty slots across every chest in the village, which
        ///     is what actually stops a villager depositing — a chest of near-full stacks still
        ///     has room, a chest of one-item stacks does not.
        /// </summary>
        private static bool IsStorageTight(
            List<Container> containers, out int freeSlots, out int totalSlots)
        {
            freeSlots = 0;
            totalSlots = 0;

            foreach (var c in containers)
            {
                var inv = c?.GetInventory();
                if (inv == null) continue;

                var slots = inv.GetWidth() * inv.GetHeight();
                totalSlots += slots;
                freeSlots += inv.GetEmptySlots();
            }

            if (totalSlots <= 0) return false;
            return freeSlots / (float)totalSlots <= StorageFreeFraction;
        }

        /// <summary>
        ///     The first work order for this villager's type whose recipe it knows but whose
        ///     ingredients have run out, phrased for the player.
        /// </summary>
        private static bool TryDescribeShortfall(
            Village village, VillagerAI ai, List<Container> containers,
            out string message, out string itemPrefab)
        {
            message = null;
            itemPrefab = null;

            var orders = ContainerScanner.FindAllWorkOrders(village, ai.VillagerType);
            foreach (var o in orders)
            {
                if (o == null || o.MaxQuantity <= 0) continue;

                // Only complain about orders still short of target — a finished order needs
                // nothing.
                var have = ContainerScanner.CountAcrossContainers(containers, o.ItemPrefabName);
                if (have >= o.MaxQuantity) continue;

                var recipe = StationMatcher.FindRecipeForNpc(o.ItemPrefabName, ai.VillagerType);
                if (recipe == null) continue;

                // Measured against what the villager can WALK TO, with every chest it can see
                // as the second opinion: standing beside a full chest saying "I'm out of
                // thistle" is worse than saying nothing — it sends the player foraging for
                // something they already have, when the fix is to move one chest.
                var reachable = ContainerScanner.FilterReachable(containers, ai.Position);
                if (!ContainerScanner.TryFindShortfall(reachable, containers, recipe, out var shortfall))
                    continue;

                var output = recipe.m_item?.m_itemData?.m_shared?.m_name;
                var outputName = string.IsNullOrEmpty(output)
                    ? o.ItemPrefabName
                    : Localization.instance.Localize(output);

                message = shortfall.IsOutOfReach
                    ? $"I can't reach the {shortfall.DisplayName} for the {outputName}!"
                    : shortfall.FoundReachable > 0
                        ? $"I need more {shortfall.DisplayName} for the {outputName} — " +
                          $"only {shortfall.FoundReachable} of {shortfall.Needed} left!"
                        : $"I'm out of {shortfall.DisplayName} for the {outputName}!";
                itemPrefab = o.ItemPrefabName;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Nearest villager to the player, but keeping the incumbent unless someone is
        ///     <see cref="SpeakerHandoverRatio" />x closer.
        /// </summary>
        private static VillagerAI ElectStorageSpeaker(string villageId, List<VillagerAI> villagers)
        {
            var nearest = NearestToPlayer(villagers);
            if (!s_storageSpeaker.TryGetValue(villageId, out var incumbentId))
            {
                s_storageSpeaker[villageId] = nearest?.UniqueId;
                return nearest;
            }

            VillagerAI incumbent = null;
            foreach (var ai in villagers)
                if (ai.UniqueId == incumbentId)
                {
                    incumbent = ai;
                    break;
                }

            if (incumbent == null || nearest == null)
            {
                s_storageSpeaker[villageId] = nearest?.UniqueId;
                return nearest;
            }

            var player = Player.m_localPlayer;
            if (player == null) return incumbent;

            var dIncumbent = Vector3.Distance(player.transform.position, incumbent.Position);
            var dNearest = Vector3.Distance(player.transform.position, nearest.Position);
            if (dNearest * SpeakerHandoverRatio >= dIncumbent) return incumbent;

            s_storageSpeaker[villageId] = nearest.UniqueId;
            return nearest;
        }

        /// <summary>
        ///     True when one of this villager's work orders cannot be deposited because its own
        ///     source chest is out of room. Uses <see cref="ContainerScanner.CanAcceptItem" /> —
        ///     the exact test the work-order scan uses for its "Output chest full" rejection — so
        ///     the alert cannot claim a chest is fine while the scan refuses to work from it.
        /// </summary>
        private static bool TryDescribeFullOutputChest(
            Village village, VillagerAI ai, List<Container> containers,
            out string message, out string itemPrefab)
        {
            message = null;
            itemPrefab = null;
            if (containers == null || containers.Count == 0) return false;

            // FindAllWorkOrders leaves SourceContainer null on purpose; the scan resolves each
            // order's deposit chest itself — the chest holding that order's token first, then the
            // nearest chest that will take the item. Ask the SAME resolver, or the alert disagrees
            // with the behaviour it describes: "some chest somewhere has room" is not the question,
            // and neither is "the chest nearest the anchor is full".
            var orders = ContainerScanner.FindAllWorkOrders(village, ai.VillagerType);
            foreach (var o in orders)
            {
                if (o == null || o.MaxQuantity <= 0) continue;
                if (WorkOrderChestPolicy.ResolveDepositChest(
                        containers, o.ItemPrefabName, o.StationName, 1, village.Anchor) != null)
                    continue;

                message = $"The chest is full — I've nowhere to put the {DisplayName(o)}!";
                itemPrefab = o.ItemPrefabName;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Localized display name for a work order's output. FindAllWorkOrders leaves
        ///     <c>ItemData</c> null (as it does SourceContainer), so fall back to resolving the
        ///     prefab through ObjectDB rather than showing the raw prefab id — "CookedMeat"
        ///     instead of "Cooked Boar Meat" reads like a bug to a player.
        /// </summary>
        private static string DisplayName(WorkOrderMatch order)
        {
            var token = order.ItemData?.m_shared?.m_name;

            if (string.IsNullOrEmpty(token) && ObjectDB.instance != null)
                token = ObjectDB.instance.GetItemPrefab(order.ItemPrefabName)
                    ?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_name;

            return string.IsNullOrEmpty(token)
                ? order.ItemPrefabName
                : Localization.instance.Localize(token);
        }

        private static VillagerAI NearestToPlayer(List<VillagerAI> villagers)
        {
            var player = Player.m_localPlayer;
            if (player == null) return villagers[0];

            VillagerAI best = null;
            var bestDist = float.MaxValue;
            foreach (var ai in villagers)
            {
                var d = Vector3.Distance(player.transform.position, ai.Position);
                if (d >= bestDist) continue;
                bestDist = d;
                best = ai;
            }

            return best ?? villagers[0];
        }

        private static VillagerAlertMarker EnsureMarker(VillagerAI ai)
        {
            var go = ai?.Villager?.gameObject;
            if (go == null) return null;
            return go.GetComponent<VillagerAlertMarker>() ?? go.AddComponent<VillagerAlertMarker>();
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_nextEvaluate = 0f;
            s_storageSpeaker.Clear();
        }
    }
}
