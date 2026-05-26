using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Handles "work_order_scan" tasks. Extracts the heavy scanning logic from
    ///     CraftingBehavior.TryScanForWork: finds containers, matches work orders,
    ///     checks quantities/ingredients/stations, and returns the fully resolved
    ///     context via callback.
    ///     Priority: Medium (2).
    /// </summary>
    [RegisterTaskHandler]
    public class WorkOrderScanHandler : ITaskHandlerWithLog
    {
        public string TaskName => "work_order_scan";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            // Parse attributes
            if (!task.Attributes.TryGetValue("villager_id", out var villagerId))
                return TaskResult.Fail("Missing villager_id");

            if (!task.Attributes.TryGetValue("villager_type", out var villagerType) ||
                string.IsNullOrEmpty(villagerType))
                return TaskResult.Fail("Missing villager_type");

            if (!TaskAttributeParser.TryParsePosition(task.Attributes, "bed", out var bedPos))
                return TaskResult.Fail("Missing or invalid bed position");

            // Look up the VillagerAI to access memory
            if (!VillagerAIManager.ActiveVillagers.TryGetValue(villagerId, out var ai))
                return TaskResult.Fail($"Villager {villagerId} not found in active villagers");

            // Find containers
            var containers = ContainerScanner.FindNearbyContainers(
                bedPos, WorkSettings.ChestScanRadius);

            if (containers.Count == 0) return TaskResult.Fail("No containers found near bed");

            // Find all matching work orders and try each until one can be fulfilled
            var allMatches = ContainerScanner.FindAllWorkOrders(containers, villagerType);

            if (allMatches == null || allMatches.Count == 0)
                // No work orders to do: ACK so the queue continues (no retry/dead-letter).
                return TaskResult.Ok();

            string lastFailReason = null;
            foreach (var match in allMatches)
            {
                // Check existing output quantity
                var existingCount = ContainerScanner.CountAcrossContainers(
                    containers, match.ItemPrefabName);

                if (existingCount >= match.MaxQuantity)
                {
                    lastFailReason = $"Already have {existingCount}/{match.MaxQuantity}";
                    continue;
                }

                // Find recipe
                var recipe = StationMatcher.FindRecipeForNpc(match.ItemPrefabName, villagerType);
                if (recipe == null)
                {
                    lastFailReason = $"No recipe for '{match.ItemPrefabName}'";
                    continue;
                }

                // Check output capacity
                var outputAmount = recipe.m_amount > 0 ? recipe.m_amount : 1;
                if (!ContainerScanner.CanAcceptItem(
                        match.SourceContainer, match.ItemPrefabName, outputAmount))
                {
                    lastFailReason = "Output chest full";
                    continue;
                }

                // Check ingredients
                var ingredients = ContainerScanner.FindIngredients(containers, recipe);
                if (ingredients == null)
                {
                    lastFailReason = $"Missing ingredients for {match.ItemPrefabName}";
                    continue;
                }

                // Find crafting station (check for physical station override from virtual recipes)
                var physicalStation = VirtualRecipeLoader.GetPhysicalStation(recipe.name);
                Vector3? stationPos;
                string stationDesc;

                // Farming recipes route to farm locations instead of crafting stations
                if (physicalStation == "farm")
                {
                    var farmContext = FarmWorkOrderHelper.BuildFarmingContext(
                        ai, match, recipe, ingredients, existingCount);
                    if (farmContext == null)
                    {
                        lastFailReason = "No farm location in memory";
                        continue;
                    }

                    activityLog.Record(villagerId, TaskName, "farm_work_matched",
                        $"matched farming work order for {match.ItemPrefabName}");

                    return TaskResult.Ok(
                        new Dictionary<string, string>
                        {
                            { "item_prefab", match.ItemPrefabName },
                            { "station_name", match.StationName },
                            { "existing_count", existingCount.ToString() },
                            { "is_farming", "true" },
                        },
                        farmContext);
                }

                CookingStation cookingStationRef = null;
                FuelNeed? fuelRequirement = null;
                Container fuelContainer = null;
                if (physicalStation == "cookingstation")
                {
                    if (StationFinder.TryFindStationAtKnownLocations<CookingStation>(
                            ai, s => StationFinder.IsCookingStationReady(s), out var pos, out var station))
                    {
                        stationPos = pos;
                        cookingStationRef = station;
                    }
                    else if (StationFinder.TryFindStationAtKnownLocations(
                                 ai, null, out pos, out station))
                    {
                        if (StationFuelHelper.DiagnoseFuelNeed(station, out var need)
                            && StationFuelHelper.FindFuelInContainers(containers, need.FuelItemPrefab, out var fc))
                        {
                            stationPos = pos;
                            cookingStationRef = station;
                            fuelRequirement = need;
                            fuelContainer = fc;
                            Plugin.Log?.LogInfo(
                                $"[WorkOrderScan] Station needs fuel ({need.FuelItemPrefab}), " +
                                "found in container. Will fuel before cooking.");
                        }
                        else
                        {
                            stationPos = null;
                        }
                    }
                    else
                    {
                        stationPos = null;
                    }

                    stationDesc = "CookingStation";
                }
                else
                {
                    if (StationFinder.TryFindStationAtKnownLocations<CraftingStation>(ai,
                            cs => cs.m_name == match.StationName, out var pos, out _))
                        stationPos = pos;
                    else
                        stationPos = null;
                    stationDesc = match.StationName;
                }

                if (!stationPos.HasValue)
                {
                    lastFailReason = $"No station '{stationDesc}' in memory";
                    continue;
                }

                // For cooking station, remember input item name so we can poll the right slot
                string cookingInputName = null;
                if (cookingStationRef != null && recipe.m_resources != null && recipe.m_resources.Length > 0 &&
                    recipe.m_resources[0].m_resItem != null)
                    cookingInputName = recipe.m_resources[0].m_resItem.gameObject.name;

                // Build the full context for the callback
                var context = new WorkOrderContext
                {
                    SourceContainer = match.SourceContainer,
                    WorkOrder = match,
                    Recipe = recipe,
                    IngredientSources = ingredients,
                    CraftStationPosition = stationPos.Value,
                    CookingStationRef = cookingStationRef,
                    CookingInputItemName = cookingInputName,
                    CraftedCount = existingCount,
                    CurrentIngredientIndex = 0,
                    FuelRequirement = fuelRequirement,
                    FuelContainer = fuelContainer,
                };

                // Log the successful scan to the activity log
                var ingredientDesc = string.Join(", ",
                    ingredients.Select(i => $"{i.Amount}x {i.PrefabName}"));
                activityLog.Record(
                    villagerId,
                    TaskName,
                    "work_order_matched",
                    $"matched work order for {match.ItemPrefabName} " +
                    $"(need: {ingredientDesc}, station: {match.StationName})");

                // Return result with context as payload for the callback
                return TaskResult.Ok(
                    new Dictionary<string, string>
                    {
                        { "item_prefab", match.ItemPrefabName },
                        { "station_name", match.StationName },
                        { "existing_count", existingCount.ToString() },
                    },
                    context);
            }

            // No work order could be fulfilled right now (station not discovered,
            // ingredients missing, output full, etc.). This is a normal "no work to do"
            // state, NOT a retriable error: return Ok with no payload so the callback
            // fires and m_scanPending is cleared, allowing the next scan cycle.
            Plugin.Log?.LogDebug(
                $"[WorkOrderScan] No actionable work order for {villagerId}: " +
                $"{lastFailReason ?? "all complete"}");
            return TaskResult.Ok();
        }
    }
}