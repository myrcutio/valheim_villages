using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.NPCs.AI;
using ValheimVillages.NPCs.AI.Work;
using ValheimVillages.NPCs.AI.Work.Farming;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Helper for building FarmingContext from work order scan results.
    /// Used by WorkOrderScanHandler when physicalStation == "farm".
    /// </summary>
    public static class FarmWorkOrderHelper
    {
        /// <summary>
        /// Build a FarmingContext for a farming work order.
        /// Checks for harvestable crops first, then planting needs.
        /// Returns null if no farming action is possible (no farm location, etc.).
        /// </summary>
        public static FarmingContext BuildFarmingContext(
            VillagerAI ai,
            WorkOrderMatch match,
            Recipe recipe,
            List<IngredientSource> ingredients,
            int existingCount)
        {
            string outputItem = match.ItemPrefabName;

            // Find a farm location in the NPC's memory
            var farmLoc = ai.Memory.KnownLocations
                .Where(l => l.Type == LocationType.Farm)
                .OrderBy(l => Vector3.Distance(ai.Memory.BedPosition, l.Position.ToVector3()))
                .FirstOrDefault();

            // Fallback: cultivated ground is terrain (Heightmap), not a discoverable object.
            // If no Farm POI exists, search for cultivated ground near the bed.
            Vector3 farmPosition;
            if (farmLoc != null)
            {
                farmPosition = farmLoc.Position.ToVector3();
            }
            else
            {
                var cultivatedPos = FindCultivatedGroundNearBed(ai.Memory.BedPosition);
                if (!cultivatedPos.HasValue)
                {
                    Plugin.Log?.LogDebug(
                        $"[FarmScan:{ai.NpcName}] No farm location or cultivated ground found");
                    return null;
                }
                farmPosition = cultivatedPos.Value;
                Plugin.Log?.LogInfo(
                    $"[FarmScan:{ai.NpcName}] Using cultivated ground at {farmPosition}");
            }

            // Check for harvestable crops first
            var harvestTarget = HarvestHelper.FindNearestHarvestableCrop(
                ai, outputItem, HarvestHelper.HarvestScanRadius);

            if (harvestTarget != null)
            {
                Plugin.Log?.LogInfo(
                    $"[FarmScan:{ai.NpcName}] Found harvestable {outputItem}");
                return new FarmingContext
                {
                    WorkOrder = match,
                    Recipe = recipe,
                    SourceContainer = match.SourceContainer,
                    IngredientSources = null, // Not planting
                    FarmPosition = farmPosition,
                    HarvestedCount = existingCount,
                    IsHarvestingPass = true,
                    CurrentHarvestTarget = harvestTarget
                };
            }

            // No harvestable crops: check if we can plant
            var piecePrefab = PlantPieceRegistry.GetPiecePrefab(outputItem);
            if (piecePrefab == null)
            {
                Plugin.Log?.LogDebug(
                    $"[FarmScan:{ai.NpcName}] No plant piece for {outputItem}");
                return null;
            }

            // Check for seeds
            if (ingredients == null || ingredients.Count == 0)
            {
                Plugin.Log?.LogDebug(
                    $"[FarmScan:{ai.NpcName}] No seeds for {outputItem}");
                return null;
            }

            // Get grow radius from the Plant component
            var plantComp = piecePrefab.GetComponent<Plant>();
            float growRadius = plantComp != null ? plantComp.m_growRadius : 0.5f;

            // Verify there's at least one valid planting position
            var testPos = PlantingHelper.FindPlantingPosition(
                farmPosition, FarmSettings.PlantSearchRadius, growRadius);
            if (!testPos.HasValue)
            {
                Plugin.Log?.LogDebug(
                    $"[FarmScan:{ai.NpcName}] No planting positions near farm");
                return null;
            }

            Plugin.Log?.LogInfo(
                $"[FarmScan:{ai.NpcName}] Planting pass for {outputItem}");
            return new FarmingContext
            {
                WorkOrder = match,
                Recipe = recipe,
                SourceContainer = match.SourceContainer,
                IngredientSources = ingredients,
                FarmPosition = farmPosition,
                PlantPiecePrefab = piecePrefab,
                PlantGrowRadius = growRadius,
                HarvestedCount = existingCount,
                IsHarvestingPass = false
            };
        }

        /// <summary>
        /// Search for cultivated ground in a spiral pattern around the bed.
        /// Cultivated ground is a Heightmap property, not a discoverable object,
        /// so normal POI discovery can't find it.
        /// </summary>
        private static Vector3? FindCultivatedGroundNearBed(Vector3 bedPos)
        {
            const float maxRadius = 30f;
            const float step = 3f;

            for (float r = 5f; r <= maxRadius; r += step)
            {
                int steps = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * r / step));
                for (int i = 0; i < steps; i++)
                {
                    float angle = (2f * Mathf.PI * i) / steps;
                    var candidate = bedPos + new Vector3(
                        Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);

                    // Snap to terrain height
                    if (ZoneSystem.instance == null) continue;
                    float height = ZoneSystem.instance.GetGroundHeight(candidate);
                    if (height <= -1000f) continue;
                    candidate.y = height;

                    if (PlantingHelper.IsCultivated(candidate))
                        return candidate;
                }
            }
            return null;
        }
    }
}
