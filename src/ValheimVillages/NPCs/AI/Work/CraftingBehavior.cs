using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using ValheimVillages.NPCs.AI.Work.Farming;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.Handlers;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Crafting behavior state machine for worker NPCs (Blacksmith, Carpenter, Farmer).
    /// Follows the same pattern as GuardBehavior: holds a VillagerAI reference,
    /// called from UpdateAI and HandleArrival.
    /// </summary>
    public partial class CraftingBehavior
    {
        private readonly VillagerAI m_ai;
        private WorkSubState m_subState = WorkSubState.Idle;
        private WorkOrderContext m_context;
        private float m_lastScanTime;
        private FarmingBehavior m_farmingBehavior;

        public CraftingBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        /// <summary>
        /// Set the farming behavior for this worker. Called by VillagerAI for Farmer NPCs.
        /// </summary>
        public void SetFarmingBehavior(FarmingBehavior fb) => m_farmingBehavior = fb;

        /// <summary>The farming behavior, if any (only Farmer NPCs).</summary>
        public FarmingBehavior FarmingBehavior => m_farmingBehavior;

        public WorkSubState SubState => m_subState;
        public bool IsWorking => m_subState != WorkSubState.Idle || (m_farmingBehavior?.IsWorking ?? false);

        /// <summary>Current item prefab name being crafted (from crafting context or farming behavior).</summary>
        public string CurrentItemPrefab =>
            m_context?.WorkOrder?.ItemPrefabName ?? m_farmingBehavior?.CurrentItemPrefab;

        /// <summary>True when we're in Crafting state at a real cooking station, waiting for food to be done (so we can use a shorter poll interval).</summary>
        public bool IsWaitingForCooking =>
            m_subState == WorkSubState.Crafting
            && m_context?.CookingStationRef != null
            && !m_context.CookingRemovalRequested;

        // Tracks whether a scan task has been enqueued and is pending
        private bool m_scanPending;

        /// <summary>
        /// Try to find a work order and begin working. Called from VillagerBehaviorLogic
        /// when the NPC is idle during daytime, or from FinishWork() to check for more work immediately.
        /// Enqueues a work_order_scan task on the global queue. The actual work
        /// starts asynchronously when the task is processed and the callback fires.
        /// Returns false because work hasn't started yet at enqueue time.
        /// </summary>
        /// <param name="ignoreScanInterval">If true, enqueue a scan even if the last scan was recent (e.g. right after finishing a task).</param>
        public bool TryScanForWork(bool ignoreScanInterval = false)
        {
            if (IsWorking) return false;
            if (m_scanPending) return false;
            if (m_ai.NpcType == null) return false;
            if (!ignoreScanInterval && Time.time - m_lastScanTime < WorkSettings.WorkScanInterval) return false;

            m_lastScanTime = Time.time;

            var bedPos = m_ai.Memory.BedPosition;
            Plugin.Log?.LogDebug(
                $"[WorkScan:{m_ai.NpcName}] Enqueueing work order scan. " +
                $"NpcType={m_ai.NpcType}, BedPos={bedPos}");

            m_scanPending = true;

            GlobalTaskQueue.Enqueue(new VillagerTask
            {
                Name = "work_order_scan",
                SourceId = m_ai.UniqueId,
                Priority = TaskPriority.Medium,
                TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                Attributes = new Dictionary<string, string>
                {
                    { "villager_id", m_ai.UniqueId },
                    { "npc_type", ((int)m_ai.NpcType.Value).ToString() },
                    { "bed_x", bedPos.x.ToString("F2", CultureInfo.InvariantCulture) },
                    { "bed_y", bedPos.y.ToString("F2", CultureInfo.InvariantCulture) },
                    { "bed_z", bedPos.z.ToString("F2", CultureInfo.InvariantCulture) }
                },
                Callback = OnWorkOrderScanResult
            });

            return false; // Work hasn't started yet; callback will initiate it
        }

        /// <summary>
        /// Callback invoked when a work_order_scan task completes successfully.
        /// Receives the fully resolved WorkOrderContext as the result payload.
        /// </summary>
        private void OnWorkOrderScanResult(TaskResult result)
        {
            m_scanPending = false;

            if (!result.Success)
            {
                Plugin.Log?.LogDebug(
                    $"[WorkScan:{m_ai.NpcName}] Scan failed: {result.Error}");
                return;
            }

            if (IsWorking) return;

            // Check if this is a farming work order
            var farmingContext = result.Payload as FarmingContext;
            if (farmingContext != null)
            {
                if (m_farmingBehavior != null)
                {
                    Plugin.Log?.LogInfo(
                        $"[WorkScan:{m_ai.NpcName}] Scan result: farming " +
                        $"{farmingContext.WorkOrder.ItemPrefabName}");
                    m_farmingBehavior.BeginFarming(farmingContext);
                }
                else
                    Plugin.Log?.LogWarning($"[WorkScan:{m_ai.NpcName}] Farming context but no FarmingBehavior");
                return;
            }

            var context = result.Payload as WorkOrderContext;
            if (context == null)
            {
                // Success with no payload = no work to do (e.g. work order already complete); just ACK and continue.
                return;
            }

            Plugin.Log?.LogInfo(
                $"[WorkScan:{m_ai.NpcName}] Scan result: starting work on " +
                $"{context.WorkOrder.ItemPrefabName} at {context.CraftStationPosition}");

            m_context = context;
            TaskQueue.ActivityLog.VillagerActivityLog.Instance.Record(
                m_ai.UniqueId, context.WorkOrder.ItemPrefabName, "start", "crafting");
            BeginGatheringIngredients();
        }

        /// <summary>
        /// Called each behavior tick while the NPC is in Working state.
        /// Handles the crafting timer sub-state and polling real cooking stations.
        /// </summary>
        public void UpdateWorkAI(float dt)
        {
            // Delegate to farming behavior if active
            if (m_farmingBehavior != null && m_farmingBehavior.IsWorking)
            {
                m_farmingBehavior.UpdateWorkAI(dt);
                return;
            }

            if (m_context == null)
            {
                AbandonWork("lost context");
                return;
            }

            if (m_subState != WorkSubState.Crafting) return;

            // Cooking station polling (extracted to CraftingCookingLogic.cs)
            if (TryPollCookingStation()) return;

            // Fixed timer for non-cooking or when station had no free slot
            float elapsed = Time.time - m_context.CraftStartTime;
            if (elapsed >= WorkSettings.CraftDuration)
                CompleteCraft();
        }

        /// <summary>
        /// Called when the NPC arrives at its movement target during Working state.
        /// </summary>
        public void HandleWorkArrival()
        {
            // Delegate to farming behavior if active
            if (m_farmingBehavior != null && m_farmingBehavior.IsWorking)
            {
                m_farmingBehavior.HandleWorkArrival();
                return;
            }

            if (m_context == null)
            {
                AbandonWork("lost context on arrival");
                return;
            }

            switch (m_subState)
            {
                case WorkSubState.GatheringIngredients:
                    OnArrivedAtIngredientChest();
                    break;
                case WorkSubState.TravelingToStation:
                    OnArrivedAtStation();
                    break;
                case WorkSubState.ReturningToChest:
                    OnArrivedAtOutputChest();
                    break;
                default:
                    Plugin.Log?.LogWarning(
                        $"[Work:{m_ai.NpcName}] Unexpected arrival in sub-state {m_subState}");
                    AbandonWork("unexpected arrival");
                    break;
            }
        }

    }
}
