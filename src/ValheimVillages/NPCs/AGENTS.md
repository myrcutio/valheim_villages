# NPCs

Keywords: NPC, villager, VillagerAI, VillagerAIManager, VillagerAIPatch, VillagerMemory, VillagerBehaviorLogic, NpcTypeRegistry, NpcTypeDefinition, NpcEquipment, VillagerPawnPatch, pawn spawning, bed claiming, MonsterAI, UpdateAI, VillagerMovement, pathfinding, NavMesh, DoorHandler, VillageNavMeshBake, NavMeshLinkPlacer, HNA, HnaRegionGraph, HnaGraphPersistence, VillagerPOIDiscovery, POI, CraftingBehavior, CraftingWorkflow, FarmingBehavior, FarmingWorkflow, ContainerScanner, StationMatcher, VillagerStation, cooking, planting, harvesting, sleep, NpcSleepPose, VillagerExploration, VillagerRestoration, hot reload, NPC definition, JSON, farmer, guard, blacksmith, carpenter, miner, scout, trader, mountaineer, shipwright, tavernkeeper

## Purpose

NPC lifecycle, AI, pathfinding, and work systems. Covers spawning villagers from pawn items, the custom AI loop that replaces MonsterAI, memory/location tracking, movement with NavMesh and HNA multi-floor navigation, and work behaviors (crafting, cooking, farming).

## Directory Structure

```
NPCs/
  NpcTypeRegistry.cs                   -- Loads NPC definitions from embedded JSON; Get(NpcType), GetByCategory()
  NpcEquipment.cs                      -- Configures Humanoid appearance from NpcTypeDefinition (gear, skin, dialog)
  NpcVisualFix.cs                      -- Visual fix patches for NPC rendering
  VillagerPawnPatch.cs                 -- Harmony patch on Humanoid.UseItem; spawns villager on pawn use at bed
  VillagerRestoration.cs               -- Restores villager components after hot reload; ConfigurePassiveAI()
  AI/
    VillagerAI.cs                      -- Main AI loop: POI discovery, behavior dispatch, movement, memory save
    VillagerAIManager.cs               -- Registry of VillagerAI instances by unique ID; [RegisterCleanup]
    VillagerAIPatch.cs                 -- Harmony prefix on MonsterAI.UpdateAI; redirects to VillagerAI
    VillagerBehaviorLogic.cs           -- Fallback behavior: location selection, exploration, time-of-day logic
    VillagerMemory.cs                  -- Known locations (bed, fire, chair, farm, patrol); ZDO persistence
    VillagerExploration.cs             -- Exploration state and target selection
    VillagerLookBehavior.cs            -- NPC look-at-player behavior
    VillagerAISleep.cs                 -- Sleep scheduling and pose management
    BehaviorSettings.cs                -- Configurable behavior parameters (distances, timers)
    NpcSleepPose.cs                    -- Sleep animation and pose controller
    Navigation/
      VillagerMovement.cs              -- Pathfinding via FindPath + MoveTowards; stall detection; fire avoidance
      VillagerPathing.cs               -- Path request abstraction
      VillagerPathingStrategies.cs     -- Strategy selection for different movement scenarios
      VillagerWaypoint.cs              -- Waypoint data structure for patrol/navigation
      VillagerPOIDiscovery.cs          -- Discovers nearby POIs (beds, fires, chairs, stations)
      VillagerMovementTest.cs          -- In-game movement test commands
      DoorHandler.cs                   -- Opens/closes doors for NPC pathfinding
      VillageNavMeshBake.cs            -- Runtime NavMesh baking for village areas
      NavMeshLinkPlacer.cs             -- Places NavMesh links for stairs, ladders, ramps
      HnaRegionGraph.cs                -- HNA region graph for multi-floor pathfinding
      HnaGraphPersistence.cs           -- ZDO persistence for HNA graph data
      HnaPerimeterRecorder.cs          -- Records perimeter data for HNA regions
      HnaSpatialDump.cs               -- Debug dump of HNA spatial data
      HnaBoundaryDump.cs              -- Debug dump of HNA boundary data
      HnaDebugVisualization.cs         -- Runtime visualization of HNA regions and links
    Work/
      VillagerStation.cs               -- Adds virtual CraftingStation to NPC ($vv_farmer, $vv_tavernkeeper)
      StationFinder.cs                 -- Finds nearby crafting stations for work
      StationMatcher.cs                -- Maps NPC types to station names; recipe lookup
      ContainerScanner.cs              -- Finds work orders in nearby containers
      CraftingBehavior.cs              -- Work-order state machine: scan -> gather -> travel -> craft
      CraftingWorkflow.cs              -- Step-by-step crafting execution
      CraftingCookingLogic.cs          -- Cooking-specific crafting logic
      CookingStationHelper.cs          -- Helpers for cooking station interaction
      CookingRescue.cs                 -- Recovery logic for stuck cooking operations
      WorkSubState.cs                  -- Sub-state enum and transitions for work behaviors
      Farming/
        FarmingBehavior.cs             -- Planting and harvesting for Farmer NPCs
        FarmingWorkflow.cs             -- Step-by-step planting execution
        FarmingHarvestWorkflow.cs       -- Step-by-step harvesting execution
        FarmingContext.cs              -- Farming context from work order scan
        FarmSettings.cs                -- Configurable farming parameters
        PlantingHelper.cs              -- Planting mechanics (seed selection, placement)
        HarvestHelper.cs               -- Harvest mechanics (crop detection, collection)
  Definitions/
    farmer.json                        -- Farmer NPC definition (behaviors: farm, craft, explore)
    guard.json                         -- Guard NPC definition (behaviors: patrol, alarm, explore)
    blacksmith.json, carpenter.json, miner.json, scout.json, trader.json,
    mountaineer.json, shipwright.json, tavernkeeper.json
```

## Key Types

| Type | Role |
|------|------|
| `VillagerAI` | Main AI loop composing behaviors, movement, memory, and POI discovery |
| `VillagerAIManager` | Static registry of active villagers by unique ID |
| `VillagerPawnPatch` | Spawns villager from pawn item use on unclaimed bed |
| `VillagerMemory` | Per-villager location knowledge persisted to ZDO |
| `NpcTypeRegistry` | Loads and caches NPC definitions from embedded JSON |
| `VillagerMovement` | Pathfinding with stall detection and fire avoidance |
| `HnaRegionGraph` | Multi-floor navigation graph (regions from beds, links for doors/stairs) |
| `CraftingBehavior` | Work-order execution: scan containers, gather materials, craft |
| `FarmingBehavior` | Planting and harvesting driven by farming work orders |
| `ContainerScanner` | Scans containers for work orders matching NPC capabilities |

## Entry Points and Registration

- `VillagerPawnPatch` -- `[HarmonyPatch(typeof(Humanoid), "UseItem")]`: intercepts pawn use on bed, spawns villager, adds components (DoorHandler, VillagerBehaviorBridge, VillagerInteract, VillagerStation).
- `VillagerAIPatch` -- `[HarmonyPatch(typeof(MonsterAI), "UpdateAI")]`: redirects to `VillagerAI.UpdateAI` for registered villagers.
- `VillagerAIManager` -- `[RegisterCleanup]` for world unload.
- `NpcTypeRegistry` -- Lazy initialization from embedded resources `ValheimVillages.NPCs.Definitions.*.json`.

## Integration

- **Behaviors/** -- `BehaviorFactory` creates `IBehavior` instances from NPC definition behavior tags.
- **TaskQueue/** -- `VillagerAI` enqueues `poi_discovery`, `poi_validation`; `CraftingBehavior` enqueues `work_order_scan`.
- **Items/** -- Pawn items trigger `VillagerPawnPatch`; work orders drive crafting/farming workflows.
- **UI/** -- `VillagerBehaviorBridge` exposes AI state to `VillagerInteract` and tab panels.
- **Villages/** -- `VillagerAIManager.GetAllBedPositions()` feeds HNA partition and NavMesh rebake.
