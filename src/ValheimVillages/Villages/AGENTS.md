# Villages

Keywords: village, village area, VillageAreaManager, VillageArea, spawn protection, enemy avoidance, EnemyAvoidancePatch, WallDetection, wall detection, door detection, point-in-polygon, patrol polygon, IsInsideAnyVillage, IsNearAnyVillage, TryGetCombinedBounds, RegisterArea, UnregisterArea, BaseAI.RandomMovement, SpawnSystem, ray casting

## Purpose

Defines and manages village areas as patrol polygons. Provides spawn suppression inside villages, enemy avoidance near village boundaries, and wall/door detection for patrol discovery.

## Directory Structure

```
Villages/
  VillageAreaManager.cs                -- Static manager: RegisterArea, UnregisterArea, IsInsideAnyVillage, bounds
  VillageArea.cs                       -- Single village area from guard patrol polygon; point-in-polygon check
  EnemyAvoidancePatch.cs               -- Harmony prefix on BaseAI.RandomMovement; nudges enemies away from villages
  WallDetection.cs                     -- IsWallPiece, IsWallCollider, RaycastForWall, RaycastForFarthestWall
```

## Key Types

| Type | Role |
|------|------|
| `VillageAreaManager` | Static registry of `VillageArea` instances; spatial queries for spawn/avoidance checks |
| `VillageArea` | Represents a protected polygon from a guard's patrol route; point-in-polygon via ray casting |
| `EnemyAvoidancePatch` | Re-rolls enemy random movement targets that fall near villages (20m buffer) |
| `WallDetection` | Identifies wall/door GameObjects by name prefix; raycasts for wall boundaries |

## Entry Points and Registration

- `VillageAreaManager.RegisterArea()` called by `GuardBehavior` after completing a patrol circuit.
- `VillageAreaManager.Clear()` has `[RegisterCleanup]` for world unload and hot reload.
- `EnemyAvoidancePatch` applied via `Harmony.PatchAll()` on `BaseAI.RandomMovement`.

## Integration

- **Behaviors/** -- `GuardBehavior` creates `VillageArea` from patrol waypoints and registers it.
- **Abilities/** -- `SpawnBlockPassiveEffect.IsActive` calls `VillageAreaManager.IsInsideAnyVillage`.
- **TaskQueue/** -- `NavMeshRebakeHandler` and `HnaPartitionHandler` use `TryGetCombinedBounds()`.
- **Behaviors/Patrol/** -- `PatrolDiscovery`, `PatrolRefiner`, `BreachDetection` use `WallDetection`.
