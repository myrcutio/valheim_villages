# Villager

Keywords: Villager, VillagerAI, VillagerAIManager, VillagerRestoration, VillagerStation, VillagerDef, VillagerRegistry, SpawnPatch, VillagerPawnPatch, POI discovery, VillagerPOIDiscovery, DoorHandler, hot reload, restoration, memory, behavior logic

## Purpose

Villager lifecycle, AI, and restoration. Owns the single spawn path (SpawnPatch), restoration after hot reload (VillagerRestoration), POI discovery (VillagerPOIDiscovery), look behavior, and Villager.Station.VillagerStation.

## Directory Structure

```
Villager/
  Villager.cs                          -- Villager component (villagerType, villagerAI); bridge from GameObject to AI
  VillagerAdapter.cs                   -- Adapter for code that needs Villager-like API
  VillagerRestoration.cs               -- Restore(MonsterAI, ZDO): identity, dialog, components; [RegisterCleanup] ClearTracking
  SpawnPatch.cs                        -- VillagerPawnPatch: Register with VillagerAIManager, LogAvailableDvergrPrefabs
  Dialog.cs                            -- Dialog/random talk wiring
  Station/
    VillagerStation.cs                 -- Initialize(villagerType), HasCraftingRecipes (tag-driven), GetStationName (from stationName field)
  Registry/
    VillagerRegistry.cs                -- Get(typeStr) -> VillagerDef
    Definitions/*.json                -- Villager type definitions (blacksmith, farmer, guard, etc.)
  AI/
    VillagerManager.cs                 -- VillagerAIManager singleton: ActiveVillagers, Register, GetOrCreate, GetAllBedPositions
    VillagerAI.cs                      -- UpdateAI loop, behavior iteration, memory save/load
    BehaviorLogic.cs                   -- VillagerBehaviorLogic (e.g. CheckShelter)
    Memory/
      VillagerMemory.cs                -- IVillagerMemory implementation; known locations, patrol state
    Discovery/
      VillagerPOIDiscovery.cs          -- DiscoverNearbyPOIs, DiscoverVisiblePOIs, ValidateKnownLocations (Transform + IVillagerMemory)
    Navigation/
      DoorHandler.cs, VillagerMovement.cs, VillagerWaypoint.cs, Region*.cs, BoundaryDump.cs, SpatialDump.cs, NavMeshLinkPlacer.cs
    Work/
      ContainerScanner.cs, StationFinder.cs, StationMatcher.cs (reads workStations from VillagerDef)
```

## Integration

- **TaskQueue/** -- POIDiscoveryHandler uses VillagerPOIDiscovery and VillagerAIManager.
- **Behaviors/** -- BehaviorFactory.CreateBehaviors(VillagerAI, tags); VillagerAI runs behaviors each tick.
- **UI/** -- VillagerBehaviorBridge uses VillagerAIManager.ActiveVillagers; `tab:workorder` tag drives Orders tab visibility via VillagerStation.HasCraftingRecipes.
- **Patches/** -- ItemPatch (ZNetSceneAwake) calls VillagerPawnPatch.LogAvailableDvergrPrefabs.
- **Items/** -- Pawns (generated from VillagerRegistry) spawn villagers via SpawnPatch; work orders drive Behaviors crafting/farming.
