using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    ///     Sub-state transition handlers for FarmingBehavior: planting pass.
    ///     Manages: gather seeds -> travel to farm -> plant.
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

            SubState = FarmSubState.GatheringSeeds;
            // Single NavTo entry point: snaps to a reachable agent-navmesh cell,
            // clears the prior path, resets the agent. (Was GetWalkableDestination
            // + raw SetState, which could land off-mesh and strand the villager.)
            if (!m_ai.NavTo(source.Container.transform.position, BehaviorState.Working, "seed chest"))
            {
                AbandonWork("no reachable approach to seed chest");
                return;
            }
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
            SubState = FarmSubState.TravelingToFarm;
            if (!m_ai.NavTo(m_context.FarmPosition, BehaviorState.Working, "farm"))
            {
                AbandonWork("no reachable approach to farm");
                return;
            }
            Plugin.Log?.LogInfo(
                $"[Farming:{m_ai.NpcName}] Walking to farm at {m_context.FarmPosition}");
        }

        private void OnArrivedAtFarm()
        {
            if (m_context.PlantPiecePrefab == null)
            {
                AbandonWork("no plant piece prefab");
                return;
            }

            TryFindAndWalkToNextPlantSpot();
        }

        private void TryFindAndWalkToNextPlantSpot()
        {
            if (m_context.SeedsGathered <= 0)
            {
                Plugin.Log?.LogInfo(
                    $"[Farming:{m_ai.NpcName}] All seeds planted " +
                    $"(total session: {m_context.PlantedThisSession})");
                FinishWork();
                return;
            }

            var plantComp = m_context.PlantPiecePrefab.GetComponent<Plant>();
            var growRadius = plantComp != null ? plantComp.m_growRadius : 0.5f;

            var pos = PlantingHelper.FindPlantingPosition(
                m_context.FarmPosition, FarmSettings.PlantSearchRadius, growRadius);

            if (!pos.HasValue)
            {
                Plugin.Log?.LogDebug(
                    $"[Farming:{m_ai.NpcName}] No more valid planting positions");
                FinishWork();
                return;
            }

            m_context.NextPlantPosition = pos.Value;
            SubState = FarmSubState.WalkingToPlantSpot;

            if (!m_ai.NavTo(pos.Value, BehaviorState.Working, "plant spot"))
            {
                // This spot isn't reachable on the agent navmesh; end planting
                // gracefully rather than stranding (FinishWork is the same path
                // used when no more positions are available).
                Plugin.Log?.LogDebug(
                    $"[Farming:{m_ai.NpcName}] Plant spot {pos.Value} unreachable; finishing.");
                FinishWork();
                return;
            }

            DebugLog.Append("FarmingWorkflow.cs:TryFindAndWalkToNextPlantSpot", "Walking to plant spot",
                new Dictionary<string, object>
                {
                    { "position", pos.Value.ToString() }, { "seedsRemaining", m_context.SeedsGathered },
                    { "plantedSoFar", m_context.PlantedThisSession },
                }, "H3", "run1");

            Plugin.Log?.LogDebug(
                $"[Farming:{m_ai.NpcName}] Walking to plant spot at {pos.Value}");
        }

        private void OnArrivedAtPlantSpot(float dt)
        {
            if (m_context.NextPlantPosition == null)
            {
                AbandonWork("lost plant target");
                return;
            }

            var dist = Vector3.Distance(
                m_ai.Instance.transform.position, m_context.NextPlantPosition.Value);

            if (dist > FarmSettings.PlantProximityRequired)
            {
                Plugin.Log?.LogDebug(
                    $"[Farming:{m_ai.NpcName}] Can't reach plant spot " +
                    $"({dist:F1}m away), skipping to find another");
                m_context.NextPlantPosition = null;
                TryFindAndWalkToNextPlantSpot();
                return;
            }

            SubState = FarmSubState.Planting;
            m_context.PlantCooldown = dt - 2.0f;
            m_ai.Instance.StopMoving();
        }

        private void UpdatePlantingCooldown(float dt)
        {
            if (SubState != FarmSubState.Planting) return;
            if (m_context.NextPlantPosition == null)
            {
                AbandonWork("lost plant target during cooldown");
                return;
            }

            m_context.PlantCooldown += 0.5f;
            if (m_context.PlantCooldown < dt)
                return;

            m_context.PlantCooldown = dt - 2.0f;
            var go = PlantingHelper.PlacePlant(
                m_context.PlantPiecePrefab, m_context.NextPlantPosition.Value);
            if (go != null)
            {
                m_context.PlantedThisSession++;
                m_context.SeedsGathered--;
                Plugin.Log?.LogInfo(
                    $"[Farming:{m_ai.NpcName}] Planted {m_context.PlantPiecePrefab.name} " +
                    $"(planted: {m_context.PlantedThisSession}, seeds left: {m_context.SeedsGathered})");
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[Farming:{m_ai.NpcName}] Failed to place plant");
            }

            m_context.NextPlantPosition = null;
            TryFindAndWalkToNextPlantSpot();
        }
    }
}