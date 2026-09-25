using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    ///     Farming behavior state machine for any NPC with the behavior:farming tag.
    ///     Handles planting seeds on cultivated ground and harvesting mature crops.
    /// </summary>
    public partial class FarmingBehavior
    {
        private readonly VillagerAI m_ai;
        private readonly IVillager m_villager;
        private FarmingContext m_context;

        public FarmingBehavior(IVillager villager)
        {
            m_villager = villager;
        }

        public FarmingBehavior(VillagerAI ai)
        {
            m_ai = ai;
            m_villager = new VillagerAdapter(ai.Villager);
        }

        public FarmSubState SubState { get; private set; } = FarmSubState.Idle;

        public bool IsWorking => SubState != FarmSubState.Idle;

        /// <summary>The item prefab name of the current farming work order, or null if idle.</summary>
        public string CurrentItemPrefab => m_context?.WorkOrder?.ItemPrefabName;

        /// <summary>
        ///     Start a farming session with the given context (from work order scan).
        ///     Called by CraftingBehavior when it detects a farming work order.
        /// </summary>
        public void BeginFarming(FarmingContext context)
        {
            if (context == null)
            {
                Plugin.Log?.LogWarning($"[Farming:{m_villager.VillagerName}] Null context");
                return;
            }

            m_context = context;

            Plugin.Log?.LogInfo(
                $"[Farming:{m_villager.VillagerName}] Starting farm work: " +
                $"{context.WorkOrder.ItemPrefabName} " +
                $"(harvested: {context.HarvestedCount}/{context.WorkOrder.MaxQuantity})");

            VillagerActivityLog.Instance.Record(
                m_villager.UniqueID, context.WorkOrder.ItemPrefabName, "start", "farming");

            // Decide: harvest first (if there are ready crops), then plant if needed
            if (context.IsHarvestingPass && context.CurrentHarvestTarget != null)
                BeginHarvestPass();
            else
                BeginGatheringSeeds();
        }

        /// <summary>
        ///     Called each behavior tick while the NPC is farming.
        ///     Handles planting timing at the farm plot.
        /// </summary>
        public void UpdateWorkAI(float dt)
        {
            if (m_context == null)
            {
                AbandonWork("lost context");
                return;
            }

            ResumeInterruptedTravel();
            UpdatePlantingCooldown(dt);
        }

        /// <summary>
        ///     A travel sub-state only advances on ARRIVAL. If something preempted the walk — a
        ///     flee takes the movement target and its Calm() drops the villager to Idle — no
        ///     arrival ever comes, and the farmer stood "WalkingToPlantSpot" doing nothing until
        ///     the stale-job timeout. Every walk here sets Working, so "travel sub-state while
        ///     Idle" means the walk was taken away: issue it again from where we now stand.
        /// </summary>
        private void ResumeInterruptedTravel()
        {
            if (m_ai.CurrentState != BehaviorState.Idle) return;

            switch (SubState)
            {
                case FarmSubState.GatheringSeeds:
                case FarmSubState.TravelingToFarm:
                case FarmSubState.WalkingToPlantSpot:
                case FarmSubState.TravelingToHarvest:
                case FarmSubState.ReturningToChest:
                    break;
                default:
                    return;
            }

            DebugLog.Event("Farming", "resume_travel",
                ("villager", m_ai.NpcName), ("subState", SubState), ("pos", m_ai.Position));

            switch (SubState)
            {
                case FarmSubState.GatheringSeeds:
                    WalkToNextSeedChest();
                    break;
                case FarmSubState.TravelingToFarm:
                    BeginTravelingToFarm();
                    break;
                case FarmSubState.WalkingToPlantSpot:
                    if (m_context.NextPlantPosition.HasValue &&
                        m_ai.NavTo(m_context.NextPlantPosition.Value, BehaviorState.Working, "plant spot"))
                        break;
                    m_context.NextPlantPosition = null;
                    TryFindAndWalkToNextPlantSpot();
                    break;
                case FarmSubState.TravelingToHarvest:
                    BeginHarvestPass();
                    break;
                case FarmSubState.ReturningToChest:
                    WalkToOutputChest();
                    break;
            }
        }

        /// <summary>
        ///     Called when the NPC arrives at its movement target during farming.
        /// </summary>
        public void HandleWorkArrival(float dt)
        {
            if (m_context == null)
            {
                AbandonWork("lost context on arrival");
                return;
            }

            switch (SubState)
            {
                case FarmSubState.GatheringSeeds:
                    OnArrivedAtSeedChest();
                    break;
                case FarmSubState.TravelingToFarm:
                    OnArrivedAtFarm();
                    break;
                case FarmSubState.WalkingToPlantSpot:
                    OnArrivedAtPlantSpot(dt);
                    break;
                case FarmSubState.Planting:
                case FarmSubState.Harvesting:
                case FarmSubState.CollectingDrops:
                case FarmSubState.Depositing:
                    break;
                case FarmSubState.TravelingToHarvest:
                    OnArrivedAtCrop();
                    break;
                case FarmSubState.ReturningToChest:
                    OnArrivedAtOutputChest();
                    break;
                default:
                    Plugin.Log?.LogWarning(
                        $"[Farming:{m_villager.VillagerName}] Unexpected arrival in {SubState}");
                    AbandonWork("unexpected arrival");
                    break;
            }
        }

        /// <summary>
        ///     Called when the NPC is stuck and cannot make progress (e.g. stuck without reaching destination).
        /// </summary>
        public void GiveUpStuckWork(string reason)
        {
            AbandonWork(reason);
        }

        private void FinishWork()
        {
            var itemName = m_context?.WorkOrder?.ItemPrefabName ?? "farming";
            Plugin.Log?.LogInfo(
                $"[Farming:{m_villager.VillagerName}] Farming session complete " +
                $"({m_context?.HarvestedCount ?? 0} harvested, " +
                $"{m_context?.PlantedThisSession ?? 0} planted)");
            VillagerActivityLog.Instance.Record(m_villager.UniqueID, itemName, "complete", "farming");
            m_context = null;
            SubState = FarmSubState.Idle;
            // Deliberately does NOT re-scan for more work. The assignment ends here and
            // the villager goes Idle; the scheduler picks what comes next on the following
            // reselect. Kicking off a self-scan here would start work behind the task
            // board's back, bypassing the reranker's choice of WHICH order to do next.
            m_ai.SetState(BehaviorState.Idle);
        }

        public void AbandonWorkPublic(string reason) => AbandonWork(reason);

        private void AbandonWork(string reason)
        {
            var taskName = m_context?.WorkOrder?.ItemPrefabName ?? "farming";
            Plugin.Log?.LogWarning(
                $"[Farming:{m_villager.VillagerName}] Abandoning farming: {reason}");
            VillagerActivityLog.Instance.Record(m_villager.UniqueID, taskName, "abandon", reason);
            m_context = null;
            SubState = FarmSubState.Idle;

            m_ai.SetState(BehaviorState.Idle);
        }
    }
}