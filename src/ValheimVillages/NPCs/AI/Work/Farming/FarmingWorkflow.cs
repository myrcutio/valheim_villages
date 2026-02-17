using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.NPCs.AI.Work.Farming
{
    /// <summary>
    /// Sub-state transition handlers for FarmingBehavior: planting pass.
    /// Manages: gather seeds -> travel to farm -> plant.
    /// </summary>
    public partial class FarmingBehavior
    {
        // ─────────────── Planting Pass ───────────────

        private void BeginGatheringSeeds()
        {
            if (m_context.IngredientSources == null || m_context.IngredientSources.Count == 0)
            {
                // No seeds available; finish (plants may still be growing)
                Plugin.Log?.LogDebug(
                    $"[Farming:{m_ai.NpcName}] No seeds to plant");
                FinishWork();
                return;
            }

            m_context.CurrentIngredientIndex = 0;
            WalkToNextSeedChest();
        }

        private void WalkToNextSeedChest()
        {
            if (m_context.CurrentIngredientIndex >= m_context.IngredientSources.Count)
            {
                BeginTravelingToFarm();
                return;
            }

            var source = m_context.IngredientSources[m_context.CurrentIngredientIndex];
            if (source.Container == null)
            {
                AbandonWork("seed container destroyed");
                return;
            }

            m_subState = FarmSubState.GatheringSeeds;
            var seedChestTarget = VillagerMovement.GetWalkableDestination(source.Container.transform.position);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(seedChestTarget, PathingStrategyRegistry.WorkerId));
            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Walking to seed chest for {source.PrefabName}");
        }

        private void OnArrivedAtSeedChest()
        {
            var source = m_context.IngredientSources[m_context.CurrentIngredientIndex];
            if (source.Container == null)
            {
                AbandonWork("seed chest inaccessible");
                return;
            }

            var singleSource = new List<IngredientSource> { source };
            ContainerScanner.RemoveIngredients(singleSource);
            m_context.SeedsGathered += source.Amount;
            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Took {source.Amount}x {source.PrefabName}");

            m_context.CurrentIngredientIndex++;
            WalkToNextSeedChest();
        }

        private void BeginTravelingToFarm()
        {
            m_subState = FarmSubState.TravelingToFarm;
            var farmTarget = VillagerMovement.GetWalkableDestination(m_context.FarmPosition);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(farmTarget, PathingStrategyRegistry.WorkerId));
            Plugin.Log?.LogInfo(
                $"[Farming:{m_ai.NpcName}] Walking to farm at {m_context.FarmPosition}");
        }

        private void OnArrivedAtFarm()
        {
            m_subState = FarmSubState.Planting;
            m_ai.Instance.StopMoving();

            if (m_context.PlantPiecePrefab == null)
            {
                AbandonWork("no plant piece prefab");
                return;
            }

            // Get the grow radius from the Plant component
            var plantComp = m_context.PlantPiecePrefab.GetComponent<Plant>();
            float growRadius = plantComp != null ? plantComp.m_growRadius : 0.5f;

            // Plant seeds in available positions
            int planted = 0;
            for (int i = 0; i < m_context.SeedsGathered; i++)
            {
                var pos = PlantingHelper.FindPlantingPosition(
                    m_context.FarmPosition, FarmSettings.PlantSearchRadius, growRadius);
                if (!pos.HasValue)
                {
                    Plugin.Log?.LogDebug(
                        $"[Farming:{m_ai.NpcName}] No more planting positions");
                    break;
                }

                var go = PlantingHelper.PlacePlant(m_context.PlantPiecePrefab, pos.Value);
                if (go != null)
                    planted++;
            }

            m_context.PlantedThisSession += planted;
            Plugin.Log?.LogInfo(
                $"[Farming:{m_ai.NpcName}] Planted {planted} " +
                $"{m_context.PlantPiecePrefab.name} (total session: {m_context.PlantedThisSession})");

            // Done planting, go idle (plants need time to grow)
            FinishWork();
        }
    }
}
