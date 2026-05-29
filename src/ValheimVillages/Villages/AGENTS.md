# Villages

Keywords: village, village area, VillageAreaManager, VillageArea, spawn protection, enemy avoidance,
EnemyAvoidancePatch, WallDetection, wall detection, door detection, point-in-polygon, patrol polygon,
IsInsideAnyVillage, IsNearAnyVillage, TryGetCombinedBounds, RegisterArea, UnregisterArea, BaseAI.RandomMovement,
SpawnSystem, ray casting

## Purpose

Defines and manages village areas as patrol polygons. Provides spawn suppression inside villages, enemy avoidance near
village boundaries, and wall/door detection for patrol discovery.

## Directory Structure

```
Villages/
  VillageAreaManager.cs                -- Static manager: RegisterArea, UnregisterArea, IsInsideAnyVillage, bounds, TryGetContainingVillageKey
  VillageArea.cs                       -- Single village area from patrol polygon; point-in-polygon check
  VillageStationRegistry.cs            -- Per-village cache of crafting/cooking/smelter stations; TryFindStation, TryResolveApproach (HNA-valid)
  VillagePoiRegistry.cs                -- Per-village cache of idle/comfort PoIs (fire/table/chair/farm); GetPois by position
  EnemyAvoidancePatch.cs               -- Harmony prefix on BaseAI.RandomMovement; nudges enemies away from villages
  WallDetection.cs                     -- IsWallPiece, IsWallCollider, RaycastForWall, RaycastForFarthestWall
```

## Key Types

| Type                  | Role                                                                                             |
|-----------------------|--------------------------------------------------------------------------------------------------|
| `VillageAreaManager`  | Static registry of `VillageArea` instances; spatial queries for spawn/avoidance checks           |
| `VillageArea`         | Represents a protected polygon from a patroller's patrol route; point-in-polygon via ray casting |
| `VillageStationRegistry` | Per-village station cache; villagers look up stations by bed position (replaces per-villager LOS) |
| `VillagePoiRegistry`  | Per-village PoI cache (fire/table/chair/farm); Explore/farming/recovery query by position         |
| `EnemyAvoidancePatch` | Re-rolls enemy random movement targets that fall near villages (20m buffer)                      |
| `WallDetection`       | Identifies wall/door GameObjects by name prefix; raycasts for wall boundaries                    |

## Entry Points and Registration

- `VillageAreaManager.RegisterArea()` called by `PatrolStateMachine` after completing a patrol circuit.
- `VillageAreaManager.Clear()` has `[RegisterCleanup]` for world unload and hot reload.
- `EnemyAvoidancePatch` applied via `Harmony.PatchAll()` on `BaseAI.RandomMovement`.

## Integration

- **Behaviors/** -- `PatrolStateMachine` creates `VillageArea` from patrol waypoints and registers it.
- `RegisterArea`/`UnregisterArea` refresh `VillageStationRegistry` and `VillagePoiRegistry` for that village.
- **Behaviors/Explore** queries `VillagePoiRegistry.GetPois`; **Behaviors/Tidy** and crafting query `VillageStationRegistry`; **Behaviors/Farming** + `FarmWorkOrderHelper` query `VillagePoiRegistry` Farm PoIs.
- **Abilities/** -- `SpawnBlockPassiveEffect.IsActive` calls `VillageAreaManager.IsInsideAnyVillage`.
- **TaskQueue/** -- `NavMeshRebakeHandler` and `RegionPartitionHandler` use `TryGetCombinedBounds()`.
- **Behaviors/Patrol/** -- `PatrolDiscovery`, `PatrolRefiner`, `BreachDetection` use `WallDetection`.
