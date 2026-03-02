using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    /// Sub-state transition handlers for FarmingBehavior: harvesting pass.
    /// Manages: travel to crop -> harvest -> collect drops -> deposit/repeat.
    /// </summary>
    public partial class FarmingBehavior
    {
        // ─────────────── Harvesting Pass ───────────────

        private void BeginHarvestPass()
        {
            if (m_context.CurrentHarvestTarget == null)
            {
                // Try to find more harvestable crops
                var crop = HarvestHelper.FindNearestHarvestableCrop(
                    m_ai, m_context.WorkOrder.ItemPrefabName, HarvestHelper.HarvestScanRadius);
                if (crop == null)
                {
                    Plugin.Log?.LogDebug(
                        $"[Farming:{m_ai.NpcName}] No more crops to harvest");
                    // Switch to planting if we have seeds and quota not met
                    if (m_context.HarvestedCount < m_context.WorkOrder.MaxQuantity)
                        BeginGatheringSeeds();
                    else
                        FinishWork();
                    return;
                }
                m_context.CurrentHarvestTarget = crop;
            }

            m_subState = FarmSubState.TravelingToHarvest;
            var cropTarget = VillagerMovement.GetWalkableDestination(m_context.CurrentHarvestTarget.transform.position);
            m_ai.SetState(BehaviorState.Working, cropTarget);
            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Walking to crop at " +
                $"{m_context.CurrentHarvestTarget.transform.position}");
        }

        private void OnArrivedAtCrop()
        {
            m_ai.Instance.StopMoving();

            var target = m_context.CurrentHarvestTarget;
            if (target == null || !target.CanBePicked())
            {
                // Crop disappeared or was picked by someone else
                m_context.CurrentHarvestTarget = null;
                BeginHarvestPass(); // Try next crop
                return;
            }

            // Harvest the crop (spawns ItemDrop on ground)
            var character = m_ai.Character as Humanoid;
            HarvestHelper.HarvestCrop(target, character);
            m_context.CurrentHarvestTarget = null;

            // Collect dropped items into "carried" count (cap per trip), then walk to container to deposit
            Vector3 harvestPos = target.transform.position;
            CollectDropsThenReturnToChest(harvestPos);
        }

        private void OnArrivedAtOutputChest()
        {
            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Arrived at output chest");

            if (m_context.CarriedHarvestCount > 0)
            {
                var container = m_context.SourceContainer;
                if (container == null)
                {
                    AbandonWork("output container lost");
                    return;
                }
                string outputName = m_context.WorkOrder.ItemPrefabName;
                int toDeposit = m_context.CarriedHarvestCount;
                if (ContainerScanner.TryDepositItem(container, outputName, toDeposit))
                {
                    m_context.HarvestedCount += toDeposit;
                    m_context.CarriedHarvestCount = 0;
                    Plugin.Log?.LogInfo(
                        $"[Farming:{m_ai.NpcName}] Deposited {toDeposit}x {outputName} " +
                        $"(total: {m_context.HarvestedCount}/{m_context.WorkOrder.MaxQuantity})");
                }
                else
                {
                    AbandonWork("chest full, cannot deposit");
                    return;
                }
            }

            if (m_context.HarvestedCount >= m_context.WorkOrder.MaxQuantity)
            {
                Plugin.Log?.LogInfo(
                    $"[Farming:{m_ai.NpcName}] Work order complete!");
                FinishWork();
                return;
            }

            // Try harvesting more
            BeginHarvestPass();
        }

        /// <summary>
        /// Collect dropped items near the harvest point into CarriedHarvestCount (capped per trip),
        /// then walk to the work order container to deposit. No teleportation.
        /// </summary>
        private void CollectDropsThenReturnToChest(Vector3 harvestPos)
        {
            string outputName = m_context.WorkOrder.ItemPrefabName;
            int spaceInCarry = FarmSettings.MaxHarvestCarryPerTrip - m_context.CarriedHarvestCount;
            if (spaceInCarry <= 0)
            {
                WalkToOutputChest();
                return;
            }

            int collected = CollectItemDropsIntoCarry(harvestPos, 3f, outputName, spaceInCarry);
            if (collected > 0)
            {
                m_context.CarriedHarvestCount += collected;
                Plugin.Log?.LogDebug(
                    $"[Farming:{m_ai.NpcName}] Carrying {collected}x {outputName} " +
                    $"(carried: {m_context.CarriedHarvestCount}, total harvested: {m_context.HarvestedCount})");
            }

            if (m_context.CarriedHarvestCount > 0)
            {
                WalkToOutputChest();
                return;
            }

            // Nothing to carry; try more harvests or finish
            if (m_context.HarvestedCount >= m_context.WorkOrder.MaxQuantity)
            {
                FinishWork();
                return;
            }
            BeginHarvestPass();
        }

        private void WalkToOutputChest()
        {
            var container = m_context.SourceContainer;
            if (container == null)
            {
                AbandonWork("output container lost");
                return;
            }
            m_subState = FarmSubState.ReturningToChest;
            var chestTarget = VillagerMovement.GetWalkableDestination(container.transform.position);
            m_ai.SetState(BehaviorState.Working, new ValheimVillages.Villager.AI.Pathfinding.VillagerWaypoint(chestTarget, ValheimVillages.Villager.AI.Pathfinding.VillagerWaypoint.DefaultStrategyId));
            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Walking to container to deposit {m_context.CarriedHarvestCount}x");
        }

        /// <summary>
        /// Collect ItemDrop objects near a position into carried count (up to maxTake), destroy the drops.
        /// Returns number added to carry.
        /// </summary>
        private static int CollectItemDropsIntoCarry(
            Vector3 center, float radius, string prefabName, int maxTake)
        {
            int total = 0;
            var colliders = Physics.OverlapSphere(center, radius);
            foreach (var col in colliders)
            {
                if (total >= maxTake) break;
                if (col == null) continue;
                var drop = col.GetComponentInParent<ItemDrop>();
                if (drop?.m_itemData?.m_dropPrefab == null) continue;
                if (drop.m_itemData.m_dropPrefab.name != prefabName) continue;
                int stack = drop.m_itemData.m_stack;
                if (stack <= 0) continue;

                int take = Mathf.Min(stack, maxTake - total);
                drop.m_itemData.m_stack -= take;
                if (drop.m_itemData.m_stack <= 0)
                    Object.Destroy(drop.gameObject);
                total += take;
            }
            return total;
        }
    }
}
