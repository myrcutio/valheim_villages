# TaskQueue

Keywords: task queue, GlobalTaskQueue, TaskHandlerRegistry, ITaskHandler, VillagerTask, TaskResult, TaskPriority, task handler, work order scan, WorkOrderScanHandler, POI discovery, POIDiscoveryHandler, breach check, BreachCheckHandler, NavMesh rebake, NavMeshRebakeHandler, HNA partition, HnaPartitionHandler, HnaGridBuilder, container scan, ContainerScanHandler, recipe discovery, RecipeDiscoveryRefreshHandler, FarmWorkOrderHelper, activity log, VillagerActivityLog, deduplication, priority tier, RegisterTaskHandler, ProcessBatch

## Purpose

Frame-budget global task queue for async work. Tasks are prioritized into tiers (High/Medium/Low), deduplicated by (Name, SourceId), and processed each frame via `ProcessBatch()`. Each handler implements `ITaskHandler` and is registered via `[RegisterTaskHandler]`.

## Directory Structure

```
TaskQueue/
  ITaskHandler.cs                      -- ITaskHandlerWithLog interface (Handle with activity log)
  TaskHandlerRegistry.cs               -- Maps task names to handler instances; [RegisterCleanup]
  GlobalTaskQueue.cs                   -- Tiered queues, dedup, ProcessBatch() called from Plugin.Update
  TaskAttributeParser.cs               -- Parses position and other attributes from task Attributes dict
  NavMeshRebakeTaskContract.cs         -- Constants for navmesh_rebake task name and attributes
  ActivityLog/
    ActivityLogEntry.cs                -- Single log entry (timestamp, handler, message)
    VillagerActivityLog.cs             -- Per-villager log of handler actions; persisted to ZDO
  Handlers/
    WorkOrderScanHandler.cs            -- work_order_scan: finds containers, matches work orders to NPC
    POIDiscoveryHandler.cs             -- poi_discovery / poi_validation: discovers nearby POIs
    BreachCheckHandler.cs              -- breach_check: checks for wall breaches at waypoints
    NavMeshRebakeHandler.cs            -- navmesh_rebake: rebuilds NavMesh for village pathfinding
    HnaPartitionHandler.cs             -- hna_partition: builds HNA region graph
    HnaGridBuilder.cs                  -- Internal grid builder for HNA partitioning
    ContainerScanHandler.cs            -- container_scan: scans containers near NPC
    RecipeDiscoveryRefreshHandler.cs   -- recipe_discovery_refresh: re-runs cultivator/cooking discovery
    FarmWorkOrderHelper.cs             -- Helper for farm work order context resolution
```

## Key Types

| Type | Role |
|------|------|
| `GlobalTaskQueue` | Tiered queue with dedup; `Enqueue(task)`, `ProcessBatch()` per frame |
| `TaskHandlerRegistry` | Maps task name strings to `ITaskHandler` instances |
| `VillagerActivityLog` | Per-villager action log persisted to ZDO; shown in Debug tab |
| `WorkOrderScanHandler` | Scans containers for work orders matching NPC station type |
| `POIDiscoveryHandler` | Discovers beds, fires, chairs, stations near villager |
| `NavMeshRebakeHandler` | Triggers runtime NavMesh rebuild for village bounds |
| `HnaPartitionHandler` | Builds HNA region graph from village area bounds |

## Entry Points and Registration

- `[RegisterTaskHandler]` attributes discovered by `AttributeScanner.RegisterTaskHandlers()`.
- `Plugin.Update` calls `GlobalTaskQueue.ProcessBatch()` each frame.
- `Plugin.Update` enqueues `recipe_discovery_refresh`, `navmesh_rebake`, `hna_partition` (Low priority) after world load.
- `TaskHandlerRegistry` has `[RegisterCleanup]` for hot-reload clearing.

## Integration

- **NPCs/** -- `VillagerAI` enqueues `poi_discovery`; `CraftingBehavior` enqueues `work_order_scan`.
- **Behaviors/** -- Guards enqueue `breach_check` via `BreachAlarmBehavior`.
- **Villages/** -- `NavMeshRebakeHandler` and `HnaPartitionHandler` use `VillageAreaManager.TryGetCombinedBounds()`.
- **Items/** -- `RecipeDiscoveryRefreshHandler` calls `VirtualRecipeLoader.RecheckDiscoveredRecipes()`.
- **UI/** -- `DebugTab` displays `VillagerActivityLog` entries.
