using UnityEngine;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Villages;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling.Producers
{
    /// <summary>
    ///     Produces one <see cref="TaskKind.CraftWork" /> task per craft-capable villager
    ///     in the village so the scheduler offers crafting/farming alongside repair instead
    ///     of it bypassing the board. Unlike repair (one task per damaged piece), the work a
    ///     crafter does is self-discovered (next chest order or farm plot), so the task is
    ///     keyed to the VILLAGER, positioned at its home anchor, and the directed
    ///     <c>CraftingBehaviorAdapter.BeginAssignment</c> commits the actual work.
    ///
    ///     <para>Priority is the worst unmet fraction across the villager type's chest work
    ///     orders, floored at <see cref="IdleFloor" />. The floor matters: it keeps a tiny
    ///     candidate alive even with no pending chest order, which is what lets the Farmer
    ///     pick up FARM work (planting/harvest, invisible to a chest-order scan) — it is the
    ///     only claimant of its own candidate. The floor sits far below a damaged piece's
    ///     repair priority, so a villager who also repairs still prefers real repair work
    ///     over an empty craft slot.</para>
    /// </summary>
    public static class CraftWorkProducer
    {
        private const string Capability = "craft";
        private const string FarmingTag = "farming";
        private const float IdleFloor = 0.05f;

        public static void Scan(Village village, Vector3 center, float now)
        {
            if (village == null) return;
            var villageId = village.VillageId;

            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai == null || !HasTag(ai, Capability)) continue;

                // Only this village's crafters (ActiveVillagers spans every loaded village).
                var v = VillageRegistry.GetVillageAt(ai.HomeAnchor);
                if (v == null || v.VillageId != villageId) continue;

                // ONE ROW PER ORDER. Collapsing every order into a single row (priority = the
                // worst deficit) meant the reranker could only choose WHETHER this villager
                // crafts, never WHICH order — that fell to the scan's first-viable loop, which
                // starved anything late in record order. Per-order rows also make the encoder's
                // spatial term meaningful: a cooking station and a farm plot are different
                // places, where before both collapsed to the villager's anchor.
                var emitted = 0;
                foreach (var o in ActionableOrders(village, ai))
                {
                    TaskBoard.Upsert(villageId, new CandidateTask
                    {
                        SourceId = "craft:" + ai.UniqueId + ":" + o.ItemPrefab,
                        Kind = TaskKind.CraftWork,
                        Position = o.Position,
                        Priority = Mathf.Max(IdleFloor, o.Deficit),
                        ExpiresAt = 0f, // no deadline
                        RequiredCapability = Capability,
                        // This row is this villager's own work queue — see CandidateTask.OwnerVillagerId.
                        OwnerVillagerId = ai.UniqueId,
                        TargetItemPrefab = o.ItemPrefab,
                        StockFraction = o.StockFraction,
                        MinShortfall = o.MinShortfall,
                        LastWorkedAt = OrderActivity.LastWorked(villageId, o.ItemPrefab),
                    });
                    emitted++;
                }

                // A villager that farms gets a floor candidate even with no actionable chest
                // order, so it keeps picking up farm work a chest-order scan can't see (null
                // TargetItemPrefab = "pick for yourself"). A craft-ONLY villager with no order
                // gets nothing — a doomed floor candidate would churn BeginAssignment every tick.
                if (emitted == 0 && HasTag(ai, FarmingTag))
                    TaskBoard.Upsert(villageId, new CandidateTask
                    {
                        SourceId = "craft:" + ai.UniqueId,
                        Kind = TaskKind.CraftWork,
                        Position = ai.HomeAnchor,
                        Priority = IdleFloor,
                        ExpiresAt = 0f,
                        RequiredCapability = Capability,
                        OwnerVillagerId = ai.UniqueId,
                        TargetItemPrefab = null,
                    });
            }
        }

        private static bool HasTag(VillagerAI ai, string tag)
        {
            foreach (var t in ai.BehaviorTags)
                if (t == tag)
                    return true;
            return false;
        }

        /// <summary>One actionable work order, with where it is done and how far behind it is.</summary>
        private readonly struct ActionableOrder
        {
            public readonly string ItemPrefab;
            public readonly float Deficit;
            public readonly Vector3 Position;
            public readonly float StockFraction;
            public readonly float MinShortfall;

            public ActionableOrder(string itemPrefab, float deficit, Vector3 position,
                float stockFraction, float minShortfall)
            {
                ItemPrefab = itemPrefab;
                Deficit = deficit;
                Position = position;
                StockFraction = stockFraction;
                MinShortfall = minShortfall;
            }
        }

        /// <summary>
        ///     Every order this villager could act on RIGHT NOW, as its own board row. Same
        ///     admission rules the collapsed scalar used: quota not met, and ingredients actually
        ///     present — the board must never advertise work the scan will then refuse, or the
        ///     villager churns BeginAssignment -> "no work payload" -> abandon.
        /// </summary>
        private static System.Collections.Generic.IEnumerable<ActionableOrder> ActionableOrders(
            Village village, VillagerAI ai)
        {
            var containers = ContainerScanner.FindVillageContainers(ai.HomeAnchor, WorkSettings.ChestScanRadius);
            var orders = ContainerScanner.FindAllWorkOrders(village, ai.VillagerType);
            if (orders == null) yield break;

            foreach (var o in orders)
            {
                if (o == null || o.MaxQuantity <= 0 || string.IsNullOrEmpty(o.ItemPrefabName)) continue;
                var have = ContainerScanner.CountAcrossContainers(containers, o.ItemPrefabName);
                if (have >= o.MaxQuantity) continue;
                if (!CanSupply(containers, o.ItemPrefabName, ai.VillagerType)) continue;

                var deficit = Mathf.Clamp01((o.MaxQuantity - have) / (float)o.MaxQuantity);
                var stock = Mathf.Clamp01(have / (float)o.MaxQuantity);
                var minShort = Mathf.Clamp01((o.MinQuantity - have) / (float)o.MaxQuantity);
                yield return new ActionableOrder(
                    o.ItemPrefabName, deficit, StationPositionFor(o.ItemPrefabName, ai),
                    stock, minShort);
            }
        }

        /// <summary>
        ///     Where this order is actually performed, so the reranker's hop/ETA terms describe the
        ///     real trip. Falls back to the villager's anchor when the station can't be resolved —
        ///     the row is still worth offering, it just scores as if the work were at home.
        /// </summary>
        private static Vector3 StationPositionFor(string itemPrefab, VillagerAI ai)
        {
            var recipe = StationMatcher.FindRecipeForNpc(itemPrefab, ai.VillagerType);
            var physical = recipe != null ? VirtualRecipeLoader.GetPhysicalStation(recipe.name) : null;

            if (physical == "cookingstation"
                && VillageStationRegistry.TryFindStation<CookingStation>(
                    ai.HomeAnchor, null, out var cookPos, out _))
                return cookPos;

            if (physical != null && physical != "farm"
                && VillageStationRegistry.TryFindStation<Smelter>(
                    ai.HomeAnchor, sm => sm != null && StationFinder.GetSmelterPrefab(physical) != null,
                    out var smeltPos, out _))
                return smeltPos;

            return ai.HomeAnchor;
        }

        /// <summary>
        ///     True if the recipe for <paramref name="itemPrefabName" /> exists and every
        ///     ingredient it needs is currently present in the villager's chests. Uses the same
        ///     scan the crafting flow itself runs, so the board's view and the behavior's view
        ///     of "can I do this?" cannot drift apart.
        /// </summary>
        private static bool CanSupply(
            System.Collections.Generic.List<Container> containers, string itemPrefabName, string villagerType)
        {
            var recipe = StationMatcher.FindRecipeForNpc(itemPrefabName, villagerType);
            if (recipe == null) return false;
            return ContainerScanner.FindIngredients(containers, recipe) != null;
        }
    }
}
