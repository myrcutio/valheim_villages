# Villager

Keywords: Villager, VillagerAI, VillagerAIManager, VillagerRestoration, VillagerStation, VillagerDef, VillagerRegistry,
SpawnPatch, VillagerPawnPatch, comfort, VillagerComfort, DoorHandler, hot reload, restoration, memory,
behavior logic

## Purpose

Villager lifecycle, AI, and restoration. Owns the single spawn path (SpawnPatch), restoration after hot reload
(VillagerRestoration), per-villager comfort sampling (VillagerComfort), look behavior, and Villager.Station.VillagerStation.
Shared points of interest are discovered at the village level (Villages/VillagePoiRegistry), not per villager.

## Directory Structure

```
Villager/
  Villager.cs                          -- Villager component (villagerType, villagerAI); bridge from GameObject to AI
  VillagerAdapter.cs                   -- Adapter for code that needs Villager-like API
  VillagerRestoration.cs               -- Restore(MonsterAI, ZDO): identity, dialog, components; idempotent per-GameObject (re-grafts after portal/zone reload)
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
      VillagerMemory.cs                -- IVillagerMemory implementation; per-villager bed + best-comfort (shared PoIs live in Villages/VillagePoiRegistry)
    Discovery/
      VillagerComfort.cs               -- UpdateExperiencedComfort: samples shelter+fire comfort into memory (PoI discovery is village-level now)
    Navigation/
      DoorHandler.cs, VillagerMovement.cs, VillagerWaypoint.cs, Region*.cs, BoundaryDump.cs, SpatialDump.cs, NavMeshLinkPlacer.cs
    Work/
      ContainerScanner.cs, StationFinder.cs, StationMatcher.cs (reads workStations from VillagerDef)
```

## Integration

- **Behaviors/** -- BehaviorFactory.CreateBehaviors (VillagerAI, tags); VillagerAI runs behaviors each tick.
- **UI/** -- VillagerBehaviorBridge uses VillagerAIManager.ActiveVillagers; `tab:workorder` tag drives Orders tab
  visibility via VillagerStation.HasCraftingRecipes.
- **Patches/** -- ItemPatch (ZNetSceneAwake) calls VillagerPawnPatch.LogAvailableDvergrPrefabs.
- **Items/** -- Pawns (generated from VillagerRegistry) spawn villagers via SpawnPatch; work orders drive Behaviors
  crafting/farming.
