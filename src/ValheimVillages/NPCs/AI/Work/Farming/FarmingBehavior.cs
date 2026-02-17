using UnityEngine;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.NPCs.AI.Work.Farming
{
    /// <summary>
    /// Farming behavior state machine for Farmer NPCs.
    /// Handles planting seeds on cultivated ground and harvesting mature crops.
    /// Follows the same pattern as CraftingBehavior.
    /// </summary>
    public partial class FarmingBehavior
    {
        private readonly VillagerAI m_ai;
        private FarmSubState m_subState = FarmSubState.Idle;
        private FarmingContext m_context;

        public FarmingBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public FarmSubState SubState => m_subState;
        public bool IsWorking => m_subState != FarmSubState.Idle;

        /// <summary>The item prefab name of the current farming work order, or null if idle.</summary>
        public string CurrentItemPrefab => m_context?.WorkOrder?.ItemPrefabName;

        /// <summary>
        /// Start a farming session with the given context (from work order scan).
        /// Called by CraftingBehavior when it detects a farming work order.
        /// </summary>
        public void BeginFarming(FarmingContext context)
        {
            if (context == null)
            {
                Plugin.Log?.LogWarning($"[Farming:{m_ai.NpcName}] Null context");
                return;
            }
            m_context = context;

            Plugin.Log?.LogInfo(
                $"[Farming:{m_ai.NpcName}] Starting farm work: " +
                $"{context.WorkOrder.ItemPrefabName} " +
                $"(harvested: {context.HarvestedCount}/{context.WorkOrder.MaxQuantity})");

            VillagerActivityLog.Instance.Record(
                m_ai.UniqueId, context.WorkOrder.ItemPrefabName, "start", "farming");

            // Decide: harvest first (if there are ready crops), then plant if needed
            if (context.IsHarvestingPass && context.CurrentHarvestTarget != null)
                BeginHarvestPass();
            else
                BeginGatheringSeeds();
        }

        /// <summary>
        /// Called each behavior tick while the NPC is farming.
        /// Handles planting timing at the farm plot.
        /// </summary>
        public void UpdateWorkAI(float dt)
        {
            if (m_context == null)
            {
                AbandonWork("lost context");
                return;
            }

            // Planting is handled in OnArrivedAtFarm (no polling needed like cooking)
            // Harvesting collection is handled in HandleWorkArrival
        }

        /// <summary>
        /// Called when the NPC arrives at its movement target during farming.
        /// </summary>
        public void HandleWorkArrival()
        {
            if (m_context == null)
            {
                AbandonWork("lost context on arrival");
                return;
            }

            switch (m_subState)
            {
                case FarmSubState.GatheringSeeds:
                    OnArrivedAtSeedChest();
                    break;
                case FarmSubState.TravelingToFarm:
                    OnArrivedAtFarm();
                    break;
                case FarmSubState.TravelingToHarvest:
                    OnArrivedAtCrop();
                    break;
                case FarmSubState.ReturningToChest:
                    OnArrivedAtOutputChest();
                    break;
                default:
                    Plugin.Log?.LogWarning(
                        $"[Farming:{m_ai.NpcName}] Unexpected arrival in {m_subState}");
                    AbandonWork("unexpected arrival");
                    break;
            }
        }

        /// <summary>
        /// Complete farming and go idle. Triggers an immediate re-scan for more work.
        /// </summary>
        /// <summary>
        /// Called when the villager is stuck pathing (e.g. 30s without reaching destination).
        /// </summary>
        public void GiveUpStuckWork(string reason)
        {
            AbandonWork(reason);
        }

        private void FinishWork()
        {
            string itemName = m_context?.WorkOrder?.ItemPrefabName ?? "farming";
            Plugin.Log?.LogInfo(
                $"[Farming:{m_ai.NpcName}] Farming session complete " +
                $"({m_context?.HarvestedCount ?? 0} harvested, " +
                $"{m_context?.PlantedThisSession ?? 0} planted)");
            VillagerActivityLog.Instance.Record(m_ai.UniqueId, itemName, "complete", "farming");
            m_context = null;
            m_subState = FarmSubState.Idle;
            m_ai.SetState(BehaviorState.Idle);
            // Trigger immediate re-scan for more work (other work orders, etc.)
            m_ai.CraftingBehavior?.TryScanForWork(ignoreScanInterval: true);
        }

        private void AbandonWork(string reason)
        {
            string taskName = m_context?.WorkOrder?.ItemPrefabName ?? "farming";
            Plugin.Log?.LogWarning(
                $"[Farming:{m_ai.NpcName}] Abandoning farming: {reason}");
            VillagerActivityLog.Instance.Record(m_ai.UniqueId, taskName, "abandon", reason);
            m_context = null;
            m_subState = FarmSubState.Idle;
            m_ai.SetState(BehaviorState.Idle);
        }
    }
}
