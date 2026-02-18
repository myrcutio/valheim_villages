# ValheimVillages.Core

Keywords: Core library, shared library, netstandard2.0, Unity-free, algorithm, FloodFill, PathScoring, BoundaryPipeline, Hausdorff, RDP simplification, Chaikin smoothing, Vec3, TagParser, tag, namespace, NpcTypeDefinition, NpcType, NpcCategory, KnownLocation, LocationType, BehaviorContext, VillagerTask, TaskResult, TaskPriority, TabDetailData, TabListItem, IBehavior, ITaskHandler, IVillagerTab, IListPanel, IContextMenu, IPassiveEffect, IAbility, RegisterTaskHandler, RegisterTab, RegisterListPanel, RegisterContextMenu, RegisterPassive, RegisterAbility, RegisterBehavior, RegisterObjectDB, RegisterCleanup, RegisterModObject, DevCommand, ModTest, attribute, registration attribute, BehaviorEnums, PatrolState, WorkState

## Purpose

Shared library (netstandard2.0) with no Unity or Valheim dependencies. Contains algorithms, data types, interfaces, enums, and registration attributes used by both the main mod assembly and the test project.

## Directory Structure

```
ValheimVillages.Core/
  Vec3.cs                              -- Read-only 3D vector (no Unity); DistXZ, Lerp, DistanceTo
  Algorithms/
    FloodFill.cs                       -- BFS region discovery from bed positions; HeightLookup delegate, barriers
    PathScoring.cs                     -- Scores waypoints vs reference paths (Hausdorff, mean, coverage)
    BoundaryPipeline.cs                -- Offline geometry: RDP simplify, Chaikin smooth, angle pruning, dedup
  Attributes/
    DevCommandAttribute.cs             -- [DevCommand] for terminal console commands
    ModTestAttribute.cs                -- [ModTest] for in-game integration test methods
    RegisterAbilityAttribute.cs        -- [RegisterAbility("id")]
    RegisterBehaviorAttribute.cs       -- [RegisterBehavior("tag")]
    RegisterCleanupAttribute.cs        -- [RegisterCleanup] for hot-reload cleanup methods
    RegisterContextMenuAttribute.cs    -- [RegisterContextMenu("id")]
    RegisterListPanelAttribute.cs      -- [RegisterListPanel("tag", "parentTab")]
    RegisterModObjectAttribute.cs      -- [RegisterModObject("name")]
    RegisterObjectDBAttribute.cs       -- [RegisterObjectDB] for ObjectDB registration callbacks
    RegisterPassiveAttribute.cs        -- [RegisterPassive("id")]
    RegisterTabAttribute.cs            -- [RegisterTab("name", Order=N)]
    RegisterTaskHandlerAttribute.cs    -- [RegisterTaskHandler]
  Data/
    BehaviorContext.cs                 -- Environment context: IsRaining, TimeOfDay, InShelter, CurrentPosition
    KnownLocation.cs                   -- Discovered location: Vec3, LocationType, ComfortValue
    NpcTypeDefinition.cs               -- JSON NPC definition: type, behaviors, tags, benefits, equipment
    TabDetailData.cs                   -- UI detail panel data (title, description, action)
    TabListItem.cs                     -- UI list item data (label, icon, tag)
    TaskResult.cs                      -- Handler result: Success, Error, Data, Payload
    VillagerTask.cs                    -- Task message: Name, SourceId, Priority, Attributes, Callback
  Enums/
    BehaviorEnums.cs                   -- PatrolState, WorkState, FarmSubState, CraftSubState enums
    NpcType.cs                         -- NpcType and NpcCategory enums (Farmer, Guard, Blacksmith, etc.)
    TaskPriority.cs                    -- High, Medium, Low priority levels
  Interfaces/
    IAbility.cs                        -- Id, DisplayName, Description
    IBehavior.cs                       -- Tag, Priority, WantsControl, Update, OnArrival, GetStatusText
    IContextMenu.cs                    -- Id
    IListPanel.cs                      -- Tag, ParentTab
    IPassiveEffect.cs                  -- Id, DisplayName, IsActive(Vec3)
    ITaskHandler.cs                    -- TaskName
    IVillagerTab.cs                    -- Name, OnDeselected
  Tags/
    TagParser.cs                       -- Parses "namespace:key=value" tags; TryParse, FilterByNamespace, HasTag
```

## Key Types

| Type | Role |
|------|------|
| `FloodFill` | BFS from bed positions respecting barriers, slopes, and radius limits |
| `PathScoring` | Compares pipeline waypoints against reference patrol paths |
| `BoundaryPipeline` | Geometry pipeline: simplify, smooth, prune, deduplicate boundary points |
| `NpcTypeDefinition` | Deserialized NPC JSON definition with type, behaviors, tags, equipment |
| `VillagerTask` | Task message for `GlobalTaskQueue` (Name, SourceId, Priority, Attributes) |
| `TagParser` | Parses structured tags with namespace:key=value format |
| `Vec3` | Unity-free 3D vector for cross-assembly position data |

## Entry Points and Registration

- This is a library with no entry points. It is referenced by the main mod assembly and the test project.
- Registration attributes defined here are discovered by `AttributeScanner` in the mod assembly.
- Output DLL deployed to `$(BepInExDir)/plugins/ValheimVillages/`.

## Integration

- **Main mod assembly** -- every module references Core interfaces, data types, and attributes.
- **tests/ValheimVillages.Tests** -- tests algorithms, data types, and registration contracts without Unity.
