using System;
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
using ValheimVillages.Villages;
using ValheimVillages.Villages.Entity;

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

            if (!TaskAttributeParser.TryParsePosition(task.Attributes, "home", out var anchorPos))
                return TaskResult.Fail("Missing or invalid anchor position");

            // Look up the VillagerAI to access memory
            if (!VillagerAIManager.ActiveVillagers.TryGetValue(villagerId, out var ai))
                return TaskResult.Fail($"Villager {villagerId} not found in active villagers");

            // Find containers
            // Village-scoped, not a radius around this villager: the chest holding an order's
            // ingredients is routinely further from one villager's anchor than the radius.
            var containers = ContainerScanner.FindVillageContainers(
                anchorPos, WorkSettings.ChestScanRadius);

            if (containers.Count == 0) return TaskResult.Fail("No containers found near anchor");

            // Resolve the village that OWNS the work-order config (Fix C). The scan runs on the
            // host, which has the village + graph hydrated. Defer (not fail) if it isn't ready
            // yet — transient bootstrap state after world load / hot reload.
            if (!VillageStationRegistry.HasVillageFor(anchorPos))
            {
                Plugin.Log?.LogInfo(
                    $"[WorkOrderScan] {ai.NpcName} deferring scan — no village registered yet at anchor {anchorPos}");
                return TaskResult.Ok();
            }

            var village = VillageRegistry.GetVillageAt(anchorPos);
            if (village == null)
            {
                Plugin.Log?.LogInfo(
                    $"[WorkOrderScan] {ai.NpcName} deferring scan — village not hydrated at anchor {anchorPos}");
                return TaskResult.Ok();
            }

            // Config now lives on the village record, not chest tokens.
            var allMatches = ContainerScanner.FindAllWorkOrders(village, villagerType);
            if (allMatches == null || allMatches.Count == 0)
                // No work orders to do: ACK so the queue continues (no retry/dead-letter).
                return TaskResult.Ok();

            // Work the LARGEST SHORTFALL first. The loop below returns on the first order it can
            // fulfil, so record order alone starves everything after the first unsatisfied entry:
            // CarrotSeeds sat at 0/20 and was never even evaluated because CookedDeerMeat at 4/20
            // came earlier in the list. Nothing is logged in that case either — the in-loop return
            // skips EmitRejections — so a starved order is invisible rather than reported.
            //
            // Deficit is (Max - have)/Max, the SAME metric CraftWorkProducer already advertises as
            // the board priority for this villager, so the board's "how badly does this crafter
            // need to work" and the scan's "which order do I pick" cannot drift apart.
            //
            // Counts are computed once here and reused in the loop: CountAcrossContainers walks
            // every container per order, and the loop needs the same number for its quota check.
            var existingCounts = new Dictionary<string, int>();
            foreach (var m in allMatches)
            {
                if (m?.ItemPrefabName == null || existingCounts.ContainsKey(m.ItemPrefabName)) continue;
                existingCounts[m.ItemPrefabName] =
                    ContainerScanner.CountAcrossContainers(containers, m.ItemPrefabName);
            }

            float DeficitOf(WorkOrderMatch m)
            {
                if (m?.ItemPrefabName == null || m.MaxQuantity <= 0) return 0f;
                var have = existingCounts.TryGetValue(m.ItemPrefabName, out var c) ? c : 0;
                return Mathf.Clamp01((m.MaxQuantity - have) / (float)m.MaxQuantity);
            }

            // Stable within equal deficits: ties keep record order so behaviour stays predictable
            // for a player who deliberately ordered their queue.
            allMatches = allMatches
                .OrderByDescending(DeficitOf)
                .ToList();

            // The scheduler picked a SPECIFIC order (one board row per order, scored by the
            // reranker). Honour it instead of re-deciding here — otherwise the reranker's choice
            // is silently overridden by this handler's own ordering and per-order scoring is
            // pointless. Empty attribute = "pick for yourself" (non-primary mode, farming floor).
            task.Attributes.TryGetValue("target_item", out var targetItem);
            if (!string.IsNullOrEmpty(targetItem))
            {
                var directed = allMatches
                    .Where(m => m.ItemPrefabName == targetItem)
                    .ToList();
                if (directed.Count == 0)
                {
                    // The row went stale between produce and scan (quota filled, order deleted,
                    // ingredients consumed). Do NOT re-decide here: the scheduler is the only
                    // thing that chooses work, and silently substituting a different order
                    // bypasses the reranker's per-order scoring and makes the board's picks
                    // untraceable — "assigned Honey" in one log line, Carrot actually worked in
                    // the next. Report no work; the producer refreshes the board and the
                    // dispatcher offers a current row on the next tick.
                    Plugin.Log?.LogInfo(
                        $"[WorkOrderScan] {ai.NpcName}: directed order '{targetItem}' is no longer " +
                        "actionable; no work this cycle (board will refresh).");
                    return TaskResult.Ok();
                }

                allMatches = directed;
            }

            var rejections = new List<RejectionRecord>();
            foreach (var match in allMatches)
            {
                // Where THIS order's output belongs: the chest holding its own token, falling
                // back to the nearest chest that will take the item. Resolved per order rather
                // than once for the whole village — a single shared deposit chest is what piled
                // every order's food into whichever unreserved box sat closest to the registry
                // while the player's labelled chests stayed empty. The nearest-container fallback
                // is only so the rejections below have a position to report when nothing has room.
                match.SourceContainer =
                    WorkOrderChestPolicy.ResolveDepositChest(
                        containers, match.ItemPrefabName, match.StationName, 1, anchorPos)
                    ?? ContainerScanner.FindNearestContainer(containers, anchorPos);

                // Check existing output quantity (precomputed above for the deficit sort).
                var existingCount = existingCounts.TryGetValue(match.ItemPrefabName, out var have)
                    ? have
                    : ContainerScanner.CountAcrossContainers(containers, match.ItemPrefabName);

                if (existingCount >= match.MaxQuantity)
                {
                    rejections.Add(new RejectionRecord
                    {
                        ItemPrefab = match.ItemPrefabName,
                        Station = match.StationName,
                        PhysicalStation = null,
                        Reason = $"Already have {existingCount}/{match.MaxQuantity}",
                        IsUnimplemented = false,
                        WorkOrderPosition = match.SourceContainer.transform.position,
                    });
                    continue;
                }

                // Find recipe
                var recipe = StationMatcher.FindRecipeForNpc(match.ItemPrefabName, villagerType);
                if (recipe == null)
                {
                    rejections.Add(new RejectionRecord
                    {
                        ItemPrefab = match.ItemPrefabName,
                        Station = match.StationName,
                        PhysicalStation = null,
                        Reason = $"No recipe for '{match.ItemPrefabName}'",
                        IsUnimplemented = false,
                        WorkOrderPosition = match.SourceContainer.transform.position,
                    });
                    continue;
                }

                // Check output capacity. Re-resolved at the real batch size: a home chest with
                // one free slot cannot take a recipe that yields three, and the chest the order
                // then spills into is a different one.
                var outputAmount = recipe.m_amount > 0 ? recipe.m_amount : 1;
                var outputChest = WorkOrderChestPolicy.ResolveDepositChest(
                    containers, match.ItemPrefabName, match.StationName, outputAmount, anchorPos);
                if (outputChest == null)
                {
                    rejections.Add(new RejectionRecord
                    {
                        ItemPrefab = match.ItemPrefabName,
                        Station = match.StationName,
                        PhysicalStation = null,
                        Reason = "Output chest full",
                        IsUnimplemented = false,
                        WorkOrderPosition = match.SourceContainer.transform.position,
                    });
                    continue;
                }

                match.SourceContainer = outputChest;

                // Find crafting station (check for physical station override from virtual recipes)
                var physicalStation = VirtualRecipeLoader.GetPhysicalStation(recipe.name);

                // Check ingredients. Farm orders are EXEMPT: a ready crop is
                // harvested with no ingredients (BuildFarmingContext checks harvest
                // before planting), and the planting fallback validates seeds
                // itself. Gating here would block harvesting a grown crop just
                // because no seeds are stocked (e.g. ready turnips with an empty
                // TurnipSeeds shelf).
                var ingredients = ContainerScanner.FindIngredients(containers, recipe);
                if (ingredients == null && physicalStation != "farm")
                {
                    rejections.Add(new RejectionRecord
                    {
                        ItemPrefab = match.ItemPrefabName,
                        Station = match.StationName,
                        PhysicalStation = null,
                        Reason = $"Missing ingredients for {match.ItemPrefabName}",
                        IsUnimplemented = false,
                        WorkOrderPosition = match.SourceContainer.transform.position,
                    });
                    continue;
                }

                Vector3? stationPos;
                string stationDesc;
                // A forage order has no station to be missing — what it lacks is a ripe plant,
                // and the blocker the player reads should say so.
                var forageOrder = false;

                // Farming recipes route to farm locations instead of crafting stations
                if (physicalStation == "farm")
                {
                    var farmContext = FarmWorkOrderHelper.BuildFarmingContext(
                        ai, match, recipe, ingredients, existingCount);
                    if (farmContext == null)
                    {
                        rejections.Add(new RejectionRecord
                        {
                            ItemPrefab = match.ItemPrefabName,
                            Station = match.StationName,
                            PhysicalStation = physicalStation,
                            Reason = "No farm location in memory",
                            IsUnimplemented = false,
                            WorkOrderPosition = match.SourceContainer.transform.position,
                        });
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
                Beehive beehiveRef = null;
                Pickable pickableRef = null;
                Smelter smelterRef = null;
                string smelterInputName = null;
                FuelNeed? fuelRequirement = null;
                Container fuelContainer = null;
                if (physicalStation == "cookingstation")
                {
                    // Select on "would the engine let us put food in?", not "is it warm enough to
                    // keep cooking" — those differ for a fire-requiring station holding fuel.
                    if (VillageStationRegistry.TryFindStation<CookingStation>(
                            anchorPos, s => StationFinder.CanAcceptItem(s), out var pos, out var station))
                    {
                        stationPos = pos;
                        cookingStationRef = station;
                    }
                    else if (VillageStationRegistry.TryFindStation<CookingStation>(
                                 anchorPos, null, out pos, out station))
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
                else if (physicalStation == BeehiveHelper.PhysicalStation)
                {
                    // Beekeeping: the "station" is whichever hive currently HAS something in
                    // it AND yields THIS order's item — piece_birdnest shares the Beehive
                    // component and yields Feathers, so an unqualified search would send a
                    // Honey order to a nest. An empty hive is not work either, so the order
                    // simply isn't offered until the bees have produced something.
                    if (BeehiveHelper.TryFindHarvestable(
                            anchorPos, WorkSettings.ChestScanRadius, match.ItemPrefabName,
                            out var hive, out var hiveApproach))
                    {
                        stationPos = hiveApproach;
                        beehiveRef = hive;
                    }
                    else
                    {
                        stationPos = null;
                    }

                    stationDesc = "Beehive";
                }
                else if (physicalStation == ForageHelper.PhysicalStation)
                {
                    // Foraging: the "station" is whichever pickable in the village is RIPE right
                    // now AND yields THIS order's item. An unripe bush is not work — the order
                    // simply isn't offered until the berries are back, the same way an empty
                    // hive isn't offered.
                    if (ForageHelper.TryFindHarvestable(
                            anchorPos, WorkSettings.ChestScanRadius, match.ItemPrefabName,
                            out var ripe, out var ripeApproach))
                    {
                        stationPos = ripeApproach;
                        pickableRef = ripe;
                    }
                    else
                    {
                        stationPos = null;
                    }

                    stationDesc = "ripe " + match.ItemPrefabName;
                    forageOrder = true;
                }
                else if (!string.IsNullOrEmpty(physicalStation)
                         && StationFinder.GetSmelterPrefab(physicalStation) != null)
                {
                    if (VillageStationRegistry.TryFindStation<Smelter>(
                            anchorPos,
                            s => s != null && PrefabNameMatches(s.gameObject.name, physicalStation),
                            out var pos,
                            out var smelter))
                    {
                        if (StationFinder.IsSmelterReady(smelter))
                        {
                            stationPos = pos;
                            smelterRef = smelter;
                        }
                        else if (StationFuelHelper.DiagnoseFuelNeed(smelter, out var need)
                                 && StationFuelHelper.FindFuelInContainers(containers, need.FuelItemPrefab, out var fc))
                        {
                            stationPos = pos;
                            smelterRef = smelter;
                            // DiagnoseFuelNeed sets FuelTargetPosition to station.transform.position (the
                            // smelter centroid, which we already established is unreachable). Replace it
                            // with the resolved approach point so the fueling leg paths somewhere valid.
                            need.FuelTargetPosition = pos;
                            fuelRequirement = need;
                            fuelContainer = fc;
                            Plugin.Log?.LogInfo(
                                $"[WorkOrderScan] Smelter ({physicalStation}) needs fuel ({need.FuelItemPrefab}), found in container. Will fuel before smelting.");
                        }
                        else
                        {
                            stationPos = null;
                        }

                        // Smelter input is the first recipe ingredient's prefab name (Smelter conversions are 1:1).
                        if (smelterRef != null && recipe.m_resources != null && recipe.m_resources.Length > 0 &&
                            recipe.m_resources[0].m_resItem != null)
                            smelterInputName = recipe.m_resources[0].m_resItem.gameObject.name;
                    }
                    else
                    {
                        stationPos = null;
                    }

                    stationDesc = physicalStation;
                }
                else
                {
                    if (VillageStationRegistry.TryFindStation<CraftingStation>(anchorPos,
                            cs => cs.m_name == match.StationName, out var pos, out _))
                        stationPos = pos;
                    else
                        stationPos = null;
                    stationDesc = match.StationName;
                }

                if (!stationPos.HasValue)
                {
                    var unimplemented = physicalStation != null
                                        && physicalStation != "farm"
                                        && physicalStation != "cookingstation"
                                        && physicalStation != BeehiveHelper.PhysicalStation
                                        && physicalStation != ForageHelper.PhysicalStation
                                        && StationFinder.GetSmelterPrefab(physicalStation) == null;
                    rejections.Add(new RejectionRecord
                    {
                        ItemPrefab = match.ItemPrefabName,
                        Station = match.StationName,
                        PhysicalStation = physicalStation,
                        Reason = forageOrder
                            ? $"No ripe {match.ItemPrefabName} in village"
                            : $"No station '{stationDesc}' in village",
                        IsUnimplemented = unimplemented,
                        WorkOrderPosition = match.SourceContainer.transform.position,
                    });
                    continue;
                }

                // In-flight quota bound (cooking orders only): items already cooking on the
                // village's stations are pending output. The earlier check (line ~75) counts
                // only DEPOSITED output, so with cooking latency several villagers pipeline raw
                // input onto stations while the deposited count is still under the cap —
                // overshooting MaxQuantity (observed 49 vs a max of 32). Re-check here, now that
                // we know it's a cooking order, including what's already on the stations.
                if (cookingStationRef != null)
                {
                    var inFlight = VillageStationRegistry.CountCookingOutputInFlight(anchorPos);
                    if (existingCount + inFlight >= match.MaxQuantity)
                    {
                        rejections.Add(new RejectionRecord
                        {
                            ItemPrefab = match.ItemPrefabName,
                            Station = match.StationName,
                            PhysicalStation = physicalStation,
                            Reason = $"Quota met incl. in-flight: {existingCount}+{inFlight}/{match.MaxQuantity}",
                            IsUnimplemented = false,
                            WorkOrderPosition = match.SourceContainer.transform.position,
                        });
                        continue;
                    }
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
                    BeehiveRef = beehiveRef,
                    IsForageOrder = forageOrder,
                    PickableRef = pickableRef,
                    // Captured now, while the plant is certainly still there: a pickable with no
                    // respawn timer destroys itself the moment it is picked.
                    PickableOutputPoint = pickableRef != null
                        ? ForageHelper.OutputPoint(pickableRef)
                        : Vector3.zero,
                    CookingInputItemName = cookingInputName,
                    CraftedCount = existingCount,
                    CurrentIngredientIndex = 0,
                    FuelRequirement = fuelRequirement,
                    FuelContainer = fuelContainer,
                    SmelterRef = smelterRef,
                    SmelterInputItemName = smelterInputName,
                    SmelterProcessedAtStart = 0,
                    SmelterRemovalRequested = false,
                    SmelterItemAlreadyInChest = false,
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

                // Staleness signal for the reranker: mark on START, not on offer — a row that is
                // offered and fizzles must still read as stale, or it stops gaining ground.
                Scheduling.OrderActivity.MarkWorked(village.VillageId, match.ItemPrefabName);

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
            EmitRejections(rejections, ai, activityLog, villagerId);

            return TaskResult.Ok();
        }

        private void EmitRejections(
            List<RejectionRecord> rejections,
            VillagerAI ai,
            VillagerActivityLog activityLog,
            string villagerId)
        {
            if (rejections.Count == 0) return;

            var anyUnimplemented = rejections.Any(r => r.IsUnimplemented);
            var lines = rejections.Select(r =>
            {
                var stationDisplay = r.PhysicalStation ?? r.Station;
                return $"  {r.ItemPrefab} [{stationDisplay}] — {r.Reason}";
            });
            var summary = $"[WorkOrderScan] {ai.NpcName} blocked on {rejections.Count} orders:\n"
                          + string.Join("\n", lines);

            if (anyUnimplemented)
                Plugin.Log?.LogWarning(summary);
            else
                Plugin.Log?.LogInfo(summary);

            foreach (var r in rejections)
            {
                var stationDisplay = r.PhysicalStation ?? r.Station;
                activityLog.RecordBlocked(villagerId, TaskName, r.ItemPrefab, stationDisplay, r.Reason, r.WorkOrderPosition);
            }
        }

        private struct RejectionRecord
        {
            public string ItemPrefab;
            public string Station;
            public string PhysicalStation;
            public string Reason;
            public bool IsUnimplemented;
            public Vector3 WorkOrderPosition;
        }

        /// <summary>
        ///     Match a GameObject instance name against a prefab name. Instances carry a
        ///     "(Clone)" suffix (e.g. "smelter(Clone)") while physicalStation strings come from
        ///     prefab discovery without the suffix.
        /// </summary>
        private static bool PrefabNameMatches(string instanceName, string prefabName)
        {
            if (string.IsNullOrEmpty(instanceName) || string.IsNullOrEmpty(prefabName)) return false;
            if (instanceName == prefabName) return true;
            var cloneIdx = instanceName.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIdx > 0) instanceName = instanceName.Substring(0, cloneIdx);
            return instanceName == prefabName;
        }
    }
}