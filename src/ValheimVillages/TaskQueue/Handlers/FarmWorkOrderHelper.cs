using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Behaviors;
using ValheimVillages.Behaviors.Farming;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Schemas;
using ValheimVillages.Villages;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Helper for building FarmingContext from work order scan results.
    ///     Used by WorkOrderScanHandler when physicalStation == "farm".
    /// </summary>
    public static class FarmWorkOrderHelper
    {
        /// <summary>
        ///     Build a FarmingContext for a farming work order.
        ///     Checks for harvestable crops first, then planting needs.
        ///     Returns null if no farming action is possible (no farm location, etc.).
        /// </summary>
        public static FarmingContext BuildFarmingContext(
            IVillagerWorkContext ai,
            WorkOrderMatch match,
            Recipe recipe,
            List<IngredientSource> ingredients,
            int existingCount)
        {
            var outputItem = match.ItemPrefabName;
            var bedPos = ai.BedPosition;

            // HARVEST FIRST: a ready crop in the village is harvestable work on its
            // own — it does NOT need a farm location. A grown crop is a Pickable_X
            // that no longer registers as a Farm PoI, so gating harvest on farm
            // detection (as planting does, below) would miss exactly the crops
            // that are ready. Anchor the scan on the bed (village centre) so the
            // farmer's wandering doesn't move it out of range.
            var harvestTarget = HarvestHelper.FindNearestHarvestableCrop(
                bedPos, outputItem, HarvestHelper.HarvestScanRadius);

            if (harvestTarget != null)
            {
                Plugin.Log?.LogInfo(
                    $"[FarmScan:{ai.NpcName}] Found harvestable {outputItem} at " +
                    $"{harvestTarget.transform.position}");
                return new FarmingContext
                {
                    WorkOrder = match,
                    Recipe = recipe,
                    SourceContainer = match.SourceContainer,
                    IngredientSources = null, // Not planting
                    FarmPosition = harvestTarget.transform.position,
                    HarvestedCount = existingCount,
                    IsHarvestingPass = true,
                    CurrentHarvestTarget = harvestTarget,
                };
            }

            // PLANTING needs a farm location. Find a Farm PoI nearest the bed, or
            // fall back to cultivated ground (terrain Heightmap, not a discoverable
            // object). No farm location and no ready crop -> nothing to do.
            var farmLoc = VillagePoiRegistry.GetPois(bedPos, LocationType.Farm)
                .OrderBy(l => Vector3.Distance(bedPos, l.Position))
                .FirstOrDefault();

            Vector3 farmPosition;
            if (farmLoc != null)
            {
                farmPosition = farmLoc.Position;
            }
            else
            {
                var cultivatedPos = FindCultivatedGroundNearBed(bedPos);
                if (!cultivatedPos.HasValue)
                {
                    Plugin.Log?.LogDebug(
                        $"[FarmScan:{ai.NpcName}] No ready crop, farm location, or cultivated ground found");
                    return null;
                }

                farmPosition = cultivatedPos.Value;
                Plugin.Log?.LogInfo(
                    $"[FarmScan:{ai.NpcName}] Using cultivated ground at {farmPosition}");
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
            var growRadius = plantComp != null ? plantComp.m_growRadius : 0.5f;

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
                IsHarvestingPass = false,
            };
        }

        /// <summary>
        ///     Search for cultivated ground in a spiral pattern around the bed.
        ///     Cultivated ground is a Heightmap property, not a discoverable object,
        ///     so normal POI discovery can't find it.
        /// </summary>
        private static Vector3? FindCultivatedGroundNearBed(Vector3 bedPos)
        {
            const float maxRadius = 30f;
            const float step = 3f;

            for (var r = 5f; r <= maxRadius; r += step)
            {
                var steps = Mathf.Max(8, Mathf.RoundToInt(2f * Mathf.PI * r / step));
                for (var i = 0; i < steps; i++)
                {
                    var angle = 2f * Mathf.PI * i / steps;
                    var candidate = bedPos + new Vector3(
                        Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);

                    // Snap to terrain height
                    if (ZoneSystem.instance == null) continue;
                    var height = ZoneSystem.instance.GetGroundHeight(candidate);
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