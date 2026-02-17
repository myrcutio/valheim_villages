using UnityEngine;
using ValheimVillages.NPCs.AI;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Sub-state transition handlers for CraftingBehavior.
    /// Manages the workflow: gather -> travel -> craft -> deposit -> repeat.
    /// </summary>
    public partial class CraftingBehavior
    {
        private void BeginGatheringIngredients()
        {
            if (m_context.IngredientSources == null || m_context.IngredientSources.Count == 0)
            {
                BeginTravelingToStation();
                return;
            }

            m_context.CurrentIngredientIndex = 0;
            WalkToNextIngredientChest();
        }

        private void WalkToNextIngredientChest()
        {
            if (m_context.CurrentIngredientIndex >= m_context.IngredientSources.Count)
            {
                BeginTravelingToStation();
                return;
            }

            var source = m_context.IngredientSources[m_context.CurrentIngredientIndex];
            if (source.Container == null)
            {
                AbandonWork("ingredient container destroyed");
                return;
            }

            m_subState = WorkSubState.GatheringIngredients;
            var targetPos = VillagerMovement.GetWalkableDestination(source.Container.transform.position);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(targetPos, PathingStrategyRegistry.WorkerId));
            Plugin.Log?.LogDebug(
                $"[Work:{m_ai.NpcName}] Walking to ingredient chest for {source.PrefabName}");
        }

        private void OnArrivedAtIngredientChest()
        {
            var source = m_context.IngredientSources[m_context.CurrentIngredientIndex];

            if (source.Container == null)
            {
                AbandonWork("ingredient chest inaccessible");
                return;
            }

            var singleSource = new System.Collections.Generic.List<IngredientSource> { source };
            ContainerScanner.RemoveIngredients(singleSource);
            Plugin.Log?.LogDebug(
                $"[Work:{m_ai.NpcName}] Took {source.Amount}x {source.PrefabName}");

            m_context.CurrentIngredientIndex++;
            WalkToNextIngredientChest();
        }

        private void BeginTravelingToStation()
        {
            m_subState = WorkSubState.TravelingToStation;
            var rawPos = m_context.CraftStationPosition;
            var stationTarget = VillagerMovement.GetWalkableDestination(rawPos);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(stationTarget, PathingStrategyRegistry.WorkerId));
            float dist = Utils.DistanceXZ(m_ai.Position, rawPos);
            Plugin.Log?.LogInfo(
                $"[Work:{m_ai.NpcName}] Walking to crafting station at " +
                $"{rawPos} (XZ dist={dist:F1}m)");

            // #region agent log
            try
            {
                string logData = "{" +
                    $"\"rawX\":{rawPos.x:F2},\"rawY\":{rawPos.y:F2},\"rawZ\":{rawPos.z:F2}" +
                    $",\"adjX\":{stationTarget.x:F2},\"adjY\":{stationTarget.y:F2},\"adjZ\":{stationTarget.z:F2}" +
                    $",\"stationType\":\"{m_context.CookingStationRef?.GetType().Name ?? "CraftingStation"}\"" +
                    "}";
                string line = "{\"hypothesisId\":\"H27\",\"location\":\"CraftingWorkflow.cs:BeginTravelingToStation\"" +
                    ",\"message\":\"Raw vs adjusted station pos\"" +
                    ",\"data\":" + logData +
                    ",\"timestamp\":" + System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "}\n";
                System.IO.File.AppendAllText("/home/benny/Projects/valheim_villages/.cursor/debug.log", line);
            }
            catch { }
            // #endregion
        }

        private void OnArrivedAtStation()
        {
            m_subState = WorkSubState.Crafting;
            // Rescue: item is already done, set start time far in the past
            // so TryPollCookingStation skips the grace period immediately.
            m_context.CraftStartTime = m_context.IsRescue ? -1000f : Time.time;
            m_context.CookingRemovalRequested = false;
            m_ai.SetState(BehaviorState.Working);
            m_ai.Instance.StopMoving();

            // When using a real cooking station, add the raw item so it cooks visually
            // (skip for rescue — item is already on the station and done)
            if (!m_context.IsRescue && m_context.CookingStationRef != null && !string.IsNullOrEmpty(m_context.CookingInputItemName))
            {
                m_context.CraftCookTimeSeconds = CookingStationHelper.GetCookTime(m_context.CookingStationRef, m_context.CookingInputItemName);
                if (CookingStationHelper.AddItem(m_context.CookingStationRef, m_context.CookingInputItemName))
                    Plugin.Log?.LogDebug(
                        $"[Work:{m_ai.NpcName}] Added {m_context.CookingInputItemName} to cooking station (cook time: {m_context.CraftCookTimeSeconds:F1}s)");
                else
                {
                    Plugin.Log?.LogWarning(
                        $"[Work:{m_ai.NpcName}] Cooking station had no free slot, simulating cook");
                    m_context.CraftCookTimeSeconds = 0f;
                }
            }
            else
            {
                Plugin.Log?.LogDebug(
                    $"[Work:{m_ai.NpcName}] Crafting {m_context.WorkOrder.ItemPrefabName}...");
            }
        }

        private void CompleteCraft()
        {
            int outputAmount = (m_context.Recipe != null && m_context.Recipe.m_amount > 0)
                ? m_context.Recipe.m_amount : 1;
            m_context.CraftedCount += outputAmount;

            Plugin.Log?.LogInfo(
                $"[Work:{m_ai.NpcName}] Crafted {outputAmount}x " +
                $"{m_context.WorkOrder.ItemPrefabName} " +
                $"(total: {m_context.CraftedCount}/{m_context.WorkOrder.MaxQuantity})");

            // Go deposit the result
            m_subState = WorkSubState.ReturningToChest;
            var rawChestPos = m_context.SourceContainer?.transform.position ?? m_ai.Memory.BedPosition;
            var chestPos = VillagerMovement.GetWalkableDestination(rawChestPos);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(chestPos, PathingStrategyRegistry.WorkerId));
        }

        private void OnArrivedAtOutputChest()
        {
            m_subState = WorkSubState.Depositing;

            int outputAmount = (m_context.Recipe != null && m_context.Recipe.m_amount > 0)
                ? m_context.Recipe.m_amount : 1;
            bool deposited = true;
            if (!m_context.CookingItemAlreadyInChest)
            {
                deposited = ContainerScanner.TryDepositItem(
                    m_context.SourceContainer, m_context.WorkOrder.ItemPrefabName, outputAmount);
            }
            else
            {
                m_context.CookingItemAlreadyInChest = false;
            }

            if (!deposited)
            {
                AbandonWork("chest full, cannot deposit");
                return;
            }

            Plugin.Log?.LogDebug(
                $"[Work:{m_ai.NpcName}] Deposited {outputAmount}x " +
                $"{m_context.WorkOrder.ItemPrefabName}");

            // Check if quota is met
            if (m_context.CraftedCount >= m_context.WorkOrder.MaxQuantity)
            {
                Plugin.Log?.LogInfo(
                    $"[Work:{m_ai.NpcName}] Work order complete! " +
                    $"Crafted {m_context.CraftedCount}x " +
                    $"{m_context.WorkOrder.ItemPrefabName}");
                FinishWork();
                return;
            }

            // Continue crafting -- check for more ingredients
            var containers = ContainerScanner.FindNearbyContainers(
                m_ai.Memory.BedPosition, WorkSettings.ChestScanRadius);

            var ingredients = ContainerScanner.FindIngredients(
                containers, m_context.Recipe);
            if (ingredients == null)
            {
                Plugin.Log?.LogDebug(
                    $"[Work:{m_ai.NpcName}] No more ingredients, stopping");
                FinishWork();
                return;
            }

            // Check chest still has space for next cycle
            int nextOutput = (m_context.Recipe != null && m_context.Recipe.m_amount > 0)
                ? m_context.Recipe.m_amount : 1;
            if (!ContainerScanner.CanAcceptItem(
                m_context.SourceContainer,
                m_context.WorkOrder.ItemPrefabName,
                nextOutput))
            {
                AbandonWork("chest full for next cycle");
                return;
            }

            m_context.IngredientSources = ingredients;
            BeginGatheringIngredients();
        }

        private void FinishWork()
        {
            string itemName = m_context?.WorkOrder?.ItemPrefabName ?? "crafting";
            bool wasRescue = m_context?.IsRescue ?? false;
            string detail = wasRescue ? "cooking_rescue" : "crafted";
            VillagerActivityLog.Instance.Record(m_ai.UniqueId, itemName, "complete", detail);
            m_context = null;
            m_subState = WorkSubState.Idle;
            m_ai.SetState(BehaviorState.Idle);
            // Prioritize rescuing any remaining done cooking items before
            // scanning for new work orders.
            if (!TryRescueCookedFood())
                TryScanForWork(ignoreScanInterval: true);
        }

        /// <summary>
        /// Called when the villager is stuck pathing (e.g. 30s without reaching destination).
        /// Delegates to farming if active, otherwise abandons crafting and records the problem.
        /// </summary>
        public void GiveUpStuckWork(string reason)
        {
            if (m_farmingBehavior != null && m_farmingBehavior.IsWorking)
            {
                m_farmingBehavior.GiveUpStuckWork(reason);
                return;
            }
            AbandonWork(reason);
        }

        private void AbandonWork(string reason)
        {
            string taskName = m_context?.WorkOrder?.ItemPrefabName ?? "crafting";
            Plugin.Log?.LogWarning(
                $"[Work:{m_ai.NpcName}] Abandoning work: {reason}");
            VillagerActivityLog.Instance.Record(m_ai.UniqueId, taskName, "abandon", reason);
            m_context = null;
            m_subState = WorkSubState.Idle;
            m_ai.SetState(BehaviorState.Idle);
        }
    }
}
