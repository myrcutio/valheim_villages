using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.Behaviors.Farming;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Behaviors.Crafting
{
    /// <summary>
    ///     Crafting behavior state machine for worker NPCs (Blacksmith, Carpenter, Farmer).
    ///     Follows the same pattern as PatrolStateMachine: holds a VillagerAI reference,
    ///     called from UpdateAI and HandleArrival.
    /// </summary>
    public partial class CraftingBehavior
    {
        private readonly VillagerAI m_ai;
        private readonly IVillager m_villager;
        private WorkOrderContext m_context;
        private float m_lastScanTime;

        // Tracks whether a scan task has been enqueued and is pending
        private bool m_scanPending;

        public CraftingBehavior(Villager.Villager villagerInstance)
        {
            m_villager = new VillagerAdapter(villagerInstance);
        }

        public CraftingBehavior(VillagerAI ai)
        {
            m_ai = ai;
            m_villager = new VillagerAdapter(ai.Villager);
        }

        private string LogName => m_ai?.NpcName ?? m_villager.VillagerName;
        private string UniqueIdForLog => m_ai?.UniqueId ?? m_villager.UniqueID;

        /// <summary>The farming behavior, if any (only Farmer NPCs).</summary>
        public FarmingBehavior FarmingBehavior { get; private set; }

        public WorkSubState SubState { get; private set; } = WorkSubState.Idle;

        public bool IsWorking => SubState != WorkSubState.Idle || (FarmingBehavior?.IsWorking ?? false);

        /// <summary>Current item prefab name being crafted (from crafting context or farming behavior).</summary>
        public string CurrentItemPrefab =>
            m_context?.WorkOrder?.ItemPrefabName ?? FarmingBehavior?.CurrentItemPrefab;

        /// <summary>
        ///     True when we're in Crafting state at a real cooking station, waiting for food to be done (so we can use a
        ///     shorter poll interval).
        /// </summary>
        public bool IsWaitingForCooking =>
            SubState == WorkSubState.Crafting
            && m_context?.CookingStationRef != null
            && !m_context.CookingRemovalRequested;

        /// <summary>
        ///     Set the farming behavior for this worker. Called by VillagerAI for Farmer NPCs.
        /// </summary>
        public void SetFarmingBehavior(FarmingBehavior fb)
        {
            FarmingBehavior = fb;
        }

        /// <summary>
        ///     Try to find a work order and begin working. Called from VillagerBehaviorLogic
        ///     when the NPC is idle during daytime, or from FinishWork() to check for more work immediately.
        ///     Enqueues a work_order_scan task on the global queue. The actual work
        ///     starts asynchronously when the task is processed and the callback fires.
        ///     Returns false because work hasn't started yet at enqueue time.
        /// </summary>
        /// <param name="ignoreScanInterval">
        ///     If true, enqueue a scan even if the last scan was recent (e.g. right after finishing a
        ///     task).
        /// </param>
        public bool TryScanForWork(bool ignoreScanInterval = false)
        {
            if (IsWorking) return false;
            if (m_scanPending) return false;
            if (m_villager.VillagerType == "villager") return false;
            if (!ignoreScanInterval && Time.time - m_lastScanTime < WorkSettings.WorkScanInterval) return false;

            m_lastScanTime = Time.time;

            var bp = m_villager.BedPosition;
            Plugin.Log?.LogDebug(
                $"[WorkScan:{m_villager.VillagerName}] Enqueueing work order scan. " +
                $"VillagerType={m_villager.VillagerType}, BedPos=({bp.x:F2},{bp.y:F2},{bp.z:F2})");

            m_scanPending = true;

            GlobalTaskQueue.Enqueue(new VillagerTask
            {
                Name = "work_order_scan",
                SourceId = m_villager.UniqueID,
                Priority = TaskPriority.Medium,
                TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                Attributes = new Dictionary<string, string>
                {
                    { "villager_id", m_villager.UniqueID },
                    { "villager_type", m_villager.VillagerType },
                    { "bed_x", bp.x.ToString("F2", CultureInfo.InvariantCulture) },
                    { "bed_y", bp.y.ToString("F2", CultureInfo.InvariantCulture) },
                    { "bed_z", bp.z.ToString("F2", CultureInfo.InvariantCulture) },
                },
                Callback = OnWorkOrderScanResult,
            });

            return false; // Work hasn't started yet; callback will initiate it
        }

        /// <summary>
        ///     Callback invoked when a work_order_scan task completes successfully.
        ///     Receives the fully resolved WorkOrderContext as the result payload.
        /// </summary>
        private void OnWorkOrderScanResult(TaskResult result)
        {
            m_scanPending = false;

            if (!result.Success)
            {
                Plugin.Log?.LogDebug(
                    $"[WorkScan:{LogName}] Scan failed: {result.Error}");
                return;
            }

            if (IsWorking) return;

            // Check if this is a farming work order
            var farmingContext = result.Payload as FarmingContext;
            if (farmingContext != null)
            {
                if (FarmingBehavior != null)
                {
                    Plugin.Log?.LogInfo(
                        $"[WorkScan:{LogName}] Scan result: farming " +
                        $"{farmingContext.WorkOrder.ItemPrefabName}");
                    FarmingBehavior.BeginFarming(farmingContext);
                }
                else
                {
                    Plugin.Log?.LogWarning($"[WorkScan:{LogName}] Farming context but no FarmingBehavior");
                }

                return;
            }

            var context = result.Payload as WorkOrderContext;
            if (context == null)
                // Success with no payload = no work to do (e.g. work order already complete); just ACK and continue.
                return;

            Plugin.Log?.LogInfo(
                $"[WorkScan:{LogName}] Scan result: starting work on " +
                $"{context.WorkOrder.ItemPrefabName} at {context.CraftStationPosition}");

            m_context = context;
            VillagerActivityLog.Instance.Record(
                UniqueIdForLog, context.WorkOrder.ItemPrefabName, "start", "crafting");
            BeginFueling();
        }

        /// <summary>
        ///     Called each behavior tick while the NPC is in Working state.
        ///     Handles the crafting timer sub-state and polling real cooking stations.
        /// </summary>
        public void UpdateWorkAI(float dt)
        {
            // Delegate to farming behavior if active
            if (FarmingBehavior != null && FarmingBehavior.IsWorking)
            {
                FarmingBehavior.UpdateWorkAI(dt);
                return;
            }

            if (m_context == null)
            {
                AbandonWork("lost context");
                return;
            }

            if (SubState != WorkSubState.Crafting) return;

            // Cooking station: poll for done items instead of using a fixed timer
            if (TryPollCookingStation()) return;

            // Fixed timer for non-cooking stations
            var elapsed = Time.time - m_context.CraftStartTime;
            if (elapsed >= WorkSettings.CraftDuration)
                CompleteCraft();
        }

        /// <summary>
        ///     Called when the NPC arrives at its movement target during Working state.
        /// </summary>
        public void HandleWorkArrival(float dt)
        {
            // Delegate to farming behavior if active
            if (FarmingBehavior != null && FarmingBehavior.IsWorking)
            {
                FarmingBehavior.HandleWorkArrival(dt);
                return;
            }

            if (m_context == null)
            {
                AbandonWork("lost context on arrival");
                return;
            }

            switch (SubState)
            {
                case WorkSubState.GatheringFuel:
                    OnArrivedAtFuelContainer();
                    break;
                case WorkSubState.FuelingStation:
                    OnArrivedAtFuelTarget();
                    break;
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
                        $"[Work:{LogName}] Unexpected arrival in sub-state {SubState}");
                    AbandonWork("unexpected arrival");
                    break;
            }
        }
    }
}