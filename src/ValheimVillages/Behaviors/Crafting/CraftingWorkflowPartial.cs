using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI;
using VillagerWaypoint = ValheimVillages.Villager.AI.Pathfinding.VillagerWaypoint;

namespace ValheimVillages.Behaviors.Crafting
{
    /// <summary>
    /// Workflow method stubs for CraftingBehavior (Villager path).
    /// Full implementation to be completed in later migration step.
    /// </summary>
    public partial class CraftingBehavior
    {
        private void AbandonWork(string reason)
        {
            m_context = null;
            m_subState = WorkSubState.Idle;
            if (m_ai != null)
                m_ai.SetState(BehaviorState.Idle, (VillagerWaypoint)null);
        }

        private void BeginGatheringIngredients()
        {
            if (m_context?.IngredientSources == null || m_context.IngredientSources.Count == 0)
            {
                BeginTravelingToStation();
                return;
            }
            m_context.CurrentIngredientIndex = 0;
            WalkToNextIngredientChest();
        }

        private bool TryPollCookingStation()
        {
            return false;
        }

        private void CompleteCraft()
        {
            m_context = null;
            m_subState = WorkSubState.Idle;
            if (m_ai != null)
                m_ai.SetState(BehaviorState.Idle, (VillagerWaypoint)null);
        }

        private void OnArrivedAtIngredientChest()
        {
            if (m_context == null || m_ai == null) return;
            if (m_context.CurrentIngredientIndex >= m_context.IngredientSources.Count)
            {
                BeginTravelingToStation();
                return;
            }
            m_context.CurrentIngredientIndex++;
            WalkToNextIngredientChest();
        }

        private void OnArrivedAtStation()
        {
            if (m_context != null)
                m_subState = WorkSubState.Crafting;
        }

        private void OnArrivedAtOutputChest()
        {
            BeginGatheringIngredients();
        }

        private void BeginTravelingToStation()
        {
            m_subState = WorkSubState.TravelingToStation;
            if (m_ai != null && m_context != null)
            {
                var stationTarget = VillagerMovement.GetWalkableDestination(m_context.CraftStationPosition);
                m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(stationTarget, VillagerWaypoint.DefaultStrategyId));
            }
        }

        private void WalkToNextIngredientChest()
        {
            if (m_context == null || m_ai == null) return;
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
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(targetPos, VillagerWaypoint.DefaultStrategyId));
        }
    }
}
