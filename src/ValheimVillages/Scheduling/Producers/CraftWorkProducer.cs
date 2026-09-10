using UnityEngine;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;
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

                var deficit = OrderDeficit(village, ai);

                // A villager that farms gets a floor candidate even with no chest order, so it
                // keeps picking up farm work a chest-order scan can't see. A craft-ONLY
                // villager with no order (e.g. a Blacksmith with an empty queue) gets nothing —
                // emitting a doomed floor candidate would churn BeginAssignment every tick.
                if (deficit <= 0f && !HasTag(ai, FarmingTag)) continue;

                TaskBoard.Upsert(villageId, new CandidateTask
                {
                    SourceId = "craft:" + ai.UniqueId,
                    Kind = TaskKind.CraftWork,
                    Position = ai.HomeAnchor,
                    Priority = Mathf.Max(IdleFloor, deficit),
                    ExpiresAt = 0f, // no deadline
                    RequiredCapability = Capability,
                    // This row is this villager's own work queue — see CandidateTask.OwnerVillagerId.
                    OwnerVillagerId = ai.UniqueId,
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

        /// <summary>
        ///     Largest unmet fraction across this villager type's chest work orders, in [0,1],
        ///     counting ONLY orders the villager could actually act on right now.
        ///
        ///     <para>An order whose ingredients are missing contributes nothing. Output
        ///     shortfall alone is not enough: a CookedMeat order sitting at 5/17 with no RawMeat
        ///     left keeps a high-priority row on the board forever, and because a directed
        ///     behavior claims the villager at dispatch step 2, the villager never falls through
        ///     to the routine tier — it churns BeginAssignment → "no work payload" → abandon
        ///     every tick and can never idle or relax. The board must only advertise work that
        ///     can be executed.</para>
        /// </summary>
        private static float OrderDeficit(Village village, VillagerAI ai)
        {
            var containers = ContainerScanner.FindNearbyContainers(ai.HomeAnchor, WorkSettings.ChestScanRadius);
            var orders = ContainerScanner.FindAllWorkOrders(village, ai.VillagerType);

            var worst = 0f;
            foreach (var o in orders)
            {
                if (o == null || o.MaxQuantity <= 0) continue;
                var have = ContainerScanner.CountAcrossContainers(containers, o.ItemPrefabName);
                if (have >= o.MaxQuantity) continue;
                if (!CanSupply(containers, o.ItemPrefabName, ai.VillagerType)) continue;

                var deficit = Mathf.Clamp01((o.MaxQuantity - have) / (float)o.MaxQuantity);
                if (deficit > worst) worst = deficit;
            }

            return worst;
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
