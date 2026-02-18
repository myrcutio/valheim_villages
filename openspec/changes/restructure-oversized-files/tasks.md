# Tasks: restructure-oversized-files

## Implementation Checklist

*Large phase with 9 sub-steps. Each sub-step is independently buildable. Verify with `dotnet build` after each sub-step.*

### Sub-step 3a: Delete VillagerDialogMenu.cs (dead code, 966 lines)

- [ ] Verify `VillagerDialogMenu.Show()` and `Open()` are never called (search codebase)
- [ ] Delete `NPCs/AI/UI/VillagerDialogMenu.cs`
- [ ] Remove `"VillagerDialogMenu"` from `ModGameObjectNames` array in `HotReloadHelper.cs` (line 39)
- [ ] Remove `VillagerDialogMenu.IsVisible` guard checks in `DialogPatches.cs` (lines 70, 87)
- [ ] Update comment reference in `VillagerBehaviorBridge.cs` (line 54)
- [ ] Run `dotnet build`

### Sub-step 3b: Split HnaPartitionHandler.cs (737 lines -> 2 files)

- [ ] Create `TaskQueue/Handlers/HnaGridBuilder.cs` (~500 lines)
- [ ] Move grid sampling, flood fill, region merging, and link detection into `HnaGridBuilder.cs`
- [ ] Trim `TaskQueue/Handlers/HnaPartitionHandler.cs` to ~200 lines (ITaskHandler implementation, orchestration, result assembly)
- [ ] Run `dotnet build`

### Sub-step 3c: Tag-Based Behavior Composition System

**Create interfaces and factory:**
- [ ] Create `Behaviors/IBehavior.cs` (~20 lines) with: `Tag`, `Priority`, `WantsControl(BehaviorContext)`, `Update(float dt)`, `OnArrival()`, `Save(ZDO)`, `Load(ZDO)`, `GetStatusText()`
- [ ] Create `Behaviors/BehaviorFactory.cs` -- tag-to-type map, `CreateBehaviors(ai, tags[])` method

**Add tags/behaviors to NPC definitions:**
- [ ] Add `behaviors` array field to `NPCs/NpcTypeDefinition.cs`
- [ ] Add `tags` array field to `NPCs/NpcTypeDefinition.cs`
- [ ] Update `NPCs/Definitions/guard.json`: `"behaviors": ["patrol", "alarm"], "tags": ["passive:spawnblock", "tab:info", "tab:debug", "listpanel:guardstatus", "listpanel:villagemap"]`
- [ ] Update `NPCs/Definitions/farmer.json`: `"behaviors": ["craft", "farm"], "tags": ["tab:info", "tab:debug", "tab:workorder"]`
- [ ] Update `NPCs/Definitions/blacksmith.json`: `"behaviors": ["craft"], "tags": ["tab:info", "tab:debug", "tab:workorder"]`
- [ ] Update `NPCs/Definitions/carpenter.json`: `"behaviors": ["craft"], "tags": [...]`
- [ ] Update `NPCs/Definitions/miner.json`: `"behaviors": ["craft"], "tags": [...]`
- [ ] Update `NPCs/Definitions/scout.json`: `"behaviors": ["patrol", "explore"], "tags": [...]`
- [ ] Update `NPCs/Definitions/mountaineer.json`: `"behaviors": ["explore"], "tags": ["teacher:ability", "ability:mountainstride", "tab:info", "tab:debug"]`
- [ ] Update `NPCs/Definitions/trader.json`: `"behaviors": [...], "tags": [...]`
- [ ] Update any remaining NPC definition JSON files with appropriate `behaviors` and `tags`

**Extract patrol behavior (from GuardBehavior):**
- [ ] Create `Behaviors/Patrol/PerimeterPatrolBehavior.cs` (~400 lines) implementing `IBehavior` (tag: `"patrol"`, priority: 30)
- [ ] Extract discovery, waypoints, circuit state machine, stall recovery from `GuardBehavior.cs`
- [ ] Rename `NPCs/AI/Guards/GuardPatrolDiscovery.cs` to `Behaviors/Patrol/PatrolDiscovery.cs` (291 lines)
- [ ] Rename `NPCs/AI/Guards/GuardPatrolRefiner.cs` to `Behaviors/Patrol/PatrolRefiner.cs` (395 lines)
- [ ] Create `Behaviors/Patrol/PatrolPersistence.cs` (~80 lines) -- generic waypoint serialization
- [ ] Move `NPCs/AI/Guards/HnaBoundaryMapper.cs` to `Behaviors/Patrol/HnaBoundaryMapper.cs`

**Extract alarm behavior (from GuardBehavior):**
- [ ] Create `Behaviors/Alarm/BreachAlarmBehavior.cs` (~120 lines) implementing `IBehavior` (tag: `"alarm"`, priority: 100)
- [ ] Extract `IsAlarmed`, `BreachPosition`, `WalkToBreach()`, `ClearBreach()` from `GuardBehavior.cs`
- [ ] Move `NPCs/AI/Guards/BreachDetection.cs` to `Behaviors/Alarm/BreachDetection.cs` (74 lines)

**Wrap existing behaviors:**
- [ ] Wrap `CraftingBehavior` in `IBehavior` adapter (tag: `"craft"`, priority: 50) in `Behaviors/Work/`
- [ ] Wrap `FarmingBehavior` in `IBehavior` adapter (tag: `"farm"`, priority: 50) in `Behaviors/Farm/`
- [ ] Wrap `VillagerExploration` in `IBehavior` adapter (tag: `"explore"`, priority: 20) in `Behaviors/Explore/`

**Refactor VillagerAI:**
- [ ] Replace `m_guardBehavior`, `m_craftingBehavior`, `m_farmingBehavior` fields with `List<IBehavior> m_behaviors`
- [ ] Refactor constructor to use `BehaviorFactory.CreateBehaviors(this, npcTypeDef.behaviors)`
- [ ] Refactor `UpdateAI()` to iterate behaviors by priority: `m_behaviors.Where(b => b.WantsControl(ctx)).MaxBy(b => b.Priority)`
- [ ] Refactor `OnArrival()` to delegate to active behavior
- [ ] Remove `IsGuard` property and all `NpcType.Guard` / `IsWorkerType` checks
- [ ] Run `dotnet build`

**Delete Guards folder:**
- [ ] Verify all Guard code has been extracted into `Behaviors/Patrol/` and `Behaviors/Alarm/`
- [ ] Delete `NPCs/AI/Guards/` folder entirely (6 files, 1,785 lines)

### Sub-step 3d: Split HnaBoundaryMapper.cs (641 lines -> 2 files)

- [ ] Create `BoundaryGeometry.cs` (~300 lines) -- edge snapping, NavMesh sampling, angle calculations, clockwise sorting
- [ ] Trim `HnaBoundaryMapper.cs` to ~300 lines -- `ComputeBoundaryWaypoints()`, edge cell iteration, waypoint assembly
- [ ] Run `dotnet build`

### Sub-step 3e: Split VillagerTabManager.cs (634 lines -> 2 files, move to UI/Core/)

- [ ] Create `UI/Core/VillagerTabManager.cs` (~300 lines) -- MonoBehaviour singleton, tab lifecycle, tab switching, tag-based discovery
- [ ] Create `UI/Core/VillagerTabRenderer.cs` (~300 lines) -- content rendering, scroll handling, layout
- [ ] Run `dotnet build`

### Sub-step 3f: Split VirtualRecipeLoader.cs (560 lines -> 2 files)

- [ ] Create `Items/VirtualRecipes/VirtualRecipeParser.cs` (~250 lines) -- JSON parsing, `LoadDefinitions()`, `TryParseRecipesArray()`, `TryParseCultivatorExclusions()`, station template management
- [ ] Trim `Items/VirtualRecipes/VirtualRecipeLoader.cs` to ~250 lines -- `RegisterAll()`, `RegisterCookingRecipesIfNeeded()`, `RecheckDiscoveredRecipes()`, recipe creation
- [ ] Run `dotnet build`

### Sub-step 3g: Split HnaRegionGraph.cs (523 lines -> 2 files)

- [ ] Create `NPCs/AI/Navigation/HnaGraphPersistence.cs` (~200 lines) -- `Serialize()`, `Restore()`, ZDO key management
- [ ] Trim `NPCs/AI/Navigation/HnaRegionGraph.cs` to ~300 lines -- graph data, queries, `SetGraph()`
- [ ] Run `dotnet build`

### Sub-step 3h: Split VillagerAI.cs (509 lines -> 2 files)

- [ ] Create `NPCs/AI/VillagerAISleep.cs` (~150 lines) -- `EnterSleepAnimation()`, `ExitSleepAnimation()`, sleep detection range, movement test delegation
- [ ] Trim `NPCs/AI/VillagerAI.cs` to ~300 lines -- core `UpdateAI()`, state management, movement delegation, memory save
- [ ] Run `dotnet build`

### Sub-step 3i: Move UI to Top-Level + Tag-Driven Component Discovery

**Move UI directory:**
- [ ] Move `NPCs/AI/UI/` contents to `UI/` top-level directory
- [ ] Organize into subdirectories: `UI/Core/`, `UI/Tabs/`, `UI/Panels/`, `UI/ContextMenus/`, `UI/Interaction/`, `UI/Patches/`
- [ ] Move `VillagerUIFactory.cs` and `VillagerUIFactory.Controls.cs` to `UI/Core/`
- [ ] Move `InfoTab.cs`, `DebugTab.cs` to `UI/Tabs/`
- [ ] Move `VillagerBehaviorBridge.cs`, `VillagerInteract.cs` to `UI/Interaction/`
- [ ] Move `WorkOrderMenu.cs`, `WorkOrderMenuBuilder.cs` to `UI/ContextMenus/`
- [ ] Update all namespaces from `ValheimVillages.NPCs.AI.UI.*` to `ValheimVillages.UI.*`

**Create new UI interfaces:**
- [ ] Create `UI/Core/IListPanel.cs` (~15 lines) -- `ParentTab`, `GetListItem()`, `GetDetail()`
- [ ] Create `UI/Core/IContextMenu.cs` (~10 lines) -- `Id`, `CanShow()`, `Show()`

**Extract guard-specific UI into panels:**
- [ ] Create `UI/Panels/GuardStatusPanel.cs` (~60 lines) -- extracted from `InfoTab.GetGuardDetail()` + guard section of `InfoTab.AddAbilityItems()`
- [ ] Create `UI/Panels/VillageMapPanel.cs` (~80 lines) -- extracted from `DebugTab.Guard.cs` (91 lines) + `DebugTab.GetMapDetail()`
- [ ] Move `GuardPatrolMapRenderer.cs` to `UI/Panels/GuardPatrolMapRenderer.cs`

**Clean up InfoTab and DebugTab:**
- [ ] Remove guard-specific sections from `InfoTab.cs` (~70 lines): `NpcType.Guard` checks, `GetGuardDetail()`
- [ ] Remove mountaineer-specific sections from `InfoTab.cs` (~30 lines): `NpcType.Mountaineer` checks, `GetMountaineerDetail()`
- [ ] Remove `DebugTab.Guard.cs` (91 lines) -- replaced by `VillageMapPanel`
- [ ] Remove `AddGuardCommands` section from `DebugTab.cs` (~10 lines)
- [ ] Remove `NpcType` imports from InfoTab, DebugTab, VillagerInteract

**Simplify VillagerInteract:**
- [ ] Replace `GetGuardStateInfo()` and `GetWorkStateInfo()` with `IBehavior.GetStatusText()` iteration
- [ ] Replace `IsGuardAlarmed()` with `bridge.GetBehavior("alarm")?.IsActive`
- [ ] Remove all NpcType-specific method branches
- [ ] Run `dotnet build`

## Dependencies

- Sub-steps 3a, 3b, 3d, 3e, 3f, 3g, 3h can be done in any order (independent file splits)
- Sub-step 3c (behavior system) should be done before 3i (UI restructure) since UI panels reference `IBehavior`
- Sub-step 3i depends on 3c and 3e (tab manager split)

## Notes

- All paths are relative to `src/ValheimVillages/`
- Net code reduction: ~1,100 lines deleted, ~180 lines added
- Priority model: alarm (100) > craft/farm (50) > patrol (30) > explore (20) > default wander
- Tags use `namespace:value` convention (e.g., `"tab:info"`, `"passive:spawnblock"`, `"ability:mountainstride"`)
- The `[RegisterBehavior]`, `[RegisterTab]`, `[RegisterListPanel]`, `[RegisterContextMenu]` attributes are created in Phase 4; in this phase, registration is done manually via BehaviorFactory and VillagerTabManager
