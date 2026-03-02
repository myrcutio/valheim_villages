# Behaviors

Keywords: behavior, patrol, alarm, craft, farming, explore, IBehavior, BehaviorFactory, RegisterBehavior, PatrolStateMachine, PerimeterPatrolBehavior, BreachAlarmBehavior, breach detection, patrol discovery, waypoint, circuit tracing, scouting, HNA boundary, patrol geometry, patrol persistence, stall detection, behavior priority, WantsControl

## Purpose

Composable NPC behaviors driven by tags in NPC JSON definitions. Each behavior has a tag, priority, and lifecycle hooks (WantsControl, Update, OnArrival). The highest-priority behavior that wants control runs each tick.

## Directory Structure

```
Behaviors/
  IBehavior.cs                         -- IBehaviorPersistence interface (Save/Load to ZDO)
  BehaviorFactory.cs                   -- Maps behavior tags to creator functions; CreateBehaviors(ai, tags)
  Explore/
    ExploreBehaviorAdapter.cs          -- [RegisterBehavior("explore")] wraps VillagerBehaviorLogic
  Work/
    CraftingBehaviorAdapter.cs         -- [RegisterBehavior("craft")] wraps CraftingBehavior
    FarmBehaviorAdapter.cs             -- [RegisterBehavior("farming")] wraps FarmingBehavior, linked to crafting
  Alarm/
    BreachAlarmBehavior.cs             -- [RegisterBehavior("alarm")] priority 100, triggers on wall breaches
    BreachDetection.cs                 -- Raycast-based breach detection at patrol waypoints
  Patrol/
    PerimeterPatrolBehavior.cs         -- [RegisterBehavior("patrol")] wraps PatrolStateMachine
    PatrolStateMachine.cs              -- State machine: Scouting -> CircuitTracing -> Patrolling <-> Alarmed
    PatrolPersistence.cs               -- ZDO save/load for waypoints, breach state, HNA graph
    PatrolDiscovery.cs                 -- Scouting and circuit-tracing algorithms for patrol route
    PatrolRefiner.cs                   -- Smoothing, simplification, and refinement of patrol paths
    HnaBoundaryMapper.cs              -- Maps HNA region boundaries to patrol-compatible waypoints
    BoundaryGeometry.cs               -- Geometric utilities for patrol boundary calculations
```

## Key Types

| Type | Role |
|------|------|
| `BehaviorFactory` | Maps tag strings to `IBehavior` creator functions; returns sorted list by priority |
| `PatrolStateMachine` | Patrol state machine with scouting, circuit tracing, patrolling, and alarm states |
| `PerimeterPatrolBehavior` | Registered adapter that wraps `PatrolStateMachine`; exposes patrol state to UI |
| `BreachAlarmBehavior` | High-priority (100) behavior that activates on wall breaches |
| `PatrolPersistence` | Persists patrol state (waypoints, breach, HNA data) to ZDO |
| `PatrolDiscovery` | Discovers patrol routes by scouting walls and tracing circuits |

## Entry Points and Registration

- `[RegisterBehavior("tag")]` attributes discovered by `AttributeScanner.RegisterBehaviors()`.
- `BehaviorFactory.CreateBehaviors(ai, behaviorTags)` called by `VillagerAI` during initialization.
- Behavior keys are merged from two sources in each NPC JSON definition:
  - Legacy `"behaviors"` array (e.g. `["craft"]`)
  - `"behavior:*"` entries in the `"tags"` array (e.g. `"behavior:farming"`)
- Behaviors sorted by `Priority`; highest-priority behavior where `WantsControl()` returns true gets `Update()`.

## Integration

- **Villager/** -- `VillagerAI` (Villager.AI) iterates behaviors each tick; `VillagerMemory` stores patrol waypoints.
- **TaskQueue/** -- Patrollers enqueue `breach_check`; workers enqueue `work_order_scan`.
- **Villages/** -- Patrollers call `VillageAreaManager.RegisterArea()` after completing a patrol circuit.
- **Attributes/** -- `[RegisterCleanup]` methods clear behavior state on hot reload.
