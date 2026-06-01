using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    ///     Sub-state transition handlers for FarmingBehavior: harvesting pass.
    ///     Manages: travel to crop -> harvest -> collect drops -> deposit/repeat.
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

            SubState = FarmSubState.TravelingToHarvest;
            if (!m_ai.NavTo(m_context.CurrentHarvestTarget.transform.position,
                    BehaviorState.Working, "crop"))
            {
                AbandonWork("no reachable approach to crop");
                return;
            }
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
                m_context.CurrentHarvestTarget = null;
                BeginHarvestPass();
                return;
            }

            DebugLog.Append("FarmingHarvestWorkflow.cs:OnArrivedAtCrop", "About to harvest crop",
                new Dictionary<string, object>
                {
                    { "targetName", target.m_itemPrefab?.name ?? "NULL" },
                    { "targetPos", target.transform.position.ToString() }, { "targetGO", target.gameObject.name },
                }, "A,D", "run1");

            var character = m_ai.Character as Humanoid;
            HarvestHelper.HarvestCrop(target, character);
            m_context.CurrentHarvestTarget = null;

            var harvestPos = target.transform.position;
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

                var outputName = m_context.WorkOrder.ItemPrefabName;
                var toDeposit = m_context.CarriedHarvestCount;
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
        ///     Collect dropped items near the harvest point into CarriedHarvestCount (capped per trip),
        ///     then walk to the work order container to deposit. No teleportation.
        /// </summary>
        private void CollectDropsThenReturnToChest(Vector3 harvestPos)
        {
            var outputName = m_context.WorkOrder.ItemPrefabName;
            var spaceInCarry = FarmSettings.MaxHarvestCarryPerTrip - m_context.CarriedHarvestCount;
            if (spaceInCarry <= 0)
            {
                WalkToOutputChest();
                return;
            }

            var collected = CollectItemDropsIntoCarry(harvestPos, 3f, outputName, spaceInCarry);
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

            SubState = FarmSubState.ReturningToChest;
            if (!m_ai.NavTo(container.transform.position, BehaviorState.Working, "deposit chest"))
            {
                AbandonWork("no reachable approach to deposit chest");
                return;
            }
            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Walking to container to deposit {m_context.CarriedHarvestCount}x");
        }

        /// <summary>
        ///     Collect ItemDrop objects near a position into carried count (up to maxTake), destroy the drops.
        ///     Returns number added to carry.
        /// </summary>
        private static int CollectItemDropsIntoCarry(
            Vector3 center, float radius, string prefabName, int maxTake)
        {
            var total = 0;
            var colliders = Physics.OverlapSphere(center, radius);

            int colCount = 0, dropCount = 0, nullPrefabCount = 0, nameMismatchCount = 0;
            var mismatchNames = new List<string>();

            foreach (var col in colliders)
            {
                if (total >= maxTake) break;
                if (col == null) continue;
                colCount++;
                var drop = col.GetComponentInParent<ItemDrop>();
                if (drop == null || drop.m_itemData == null) continue;
                dropCount++;
                if (drop.m_itemData.m_dropPrefab == null)
                {
                    nullPrefabCount++;
                    mismatchNames.Add("NULL_PREFAB|go=" + drop.gameObject.name);
                    continue;
                }

                if (drop.m_itemData.m_dropPrefab.name != prefabName)
                {
                    nameMismatchCount++;
                    mismatchNames.Add(drop.m_itemData.m_dropPrefab.name + "|go=" + drop.gameObject.name);
                    continue;
                }

                var stack = drop.m_itemData.m_stack;
                if (stack <= 0) continue;

                var take = Mathf.Min(stack, maxTake - total);
                drop.m_itemData.m_stack -= take;
                if (drop.m_itemData.m_stack <= 0)
                    Object.Destroy(drop.gameObject);
                total += take;
            }

            DebugLog.Append("FarmingHarvestWorkflow.cs:CollectItemDropsIntoCarry", "Collect result",
                new Dictionary<string, object>
                {
                    { "prefabName", prefabName }, { "maxTake", maxTake }, { "totalCollected", total },
                    { "collidersFound", colCount }, { "itemDropsFound", dropCount },
                    { "nullPrefabCount", nullPrefabCount }, { "nameMismatchCount", nameMismatchCount },
                    { "mismatchNames", string.Join(";", mismatchNames) }, { "center", center.ToString() },
                    { "radius", radius },
                }, "A,D", "run1");

            return total;
        }
    }
}