# Change Proposal: restructure-oversized-files

## Intent

Split all files exceeding ~200 lines, delete dead code (VillagerDialogMenu, 966 lines), introduce a tag-based behavior composition system (eliminating the `AI/Guards/` folder entirely), and move UI code to a top-level `UI/` directory with tag-driven component discovery. This is the largest phase -- it removes ~1,100 lines and introduces composable behavior and UI architectures.

## Scope

- Delete `VillagerDialogMenu.cs` (966 lines of dead code) and clean up references
- Split 6 oversized files into 12 smaller files (each under ~300 lines)
- Create `IBehavior` interface, `BehaviorFactory`, and extract guard behaviors into `Behaviors/` folder
- Add `behaviors` and `tags` arrays to NPC JSON definitions
- Refactor `VillagerAI` to dispatch via `BehaviorFactory` instead of hardcoded type checks
- Move `NPCs/AI/UI/` to top-level `UI/` with new `IListPanel` and `IContextMenu` interfaces
- Extract guard-specific UI into self-registering `GuardStatusPanel` and `VillageMapPanel`
- Delete `AI/Guards/` folder (1,785 lines decomposed into `Behaviors/Patrol/` + `Behaviors/Alarm/`)
- Out of scope: Attribute system (Phase 4), assembly split (Phase 6), hot-reload simplification (Phase 5)

## Affected Specs

None. Behavioral outcomes are preserved; only the internal structure changes.

## Existing Code to Modify

All paths relative to `src/ValheimVillages/`:

**Dead code removal:**
- `NPCs/AI/UI/VillagerDialogMenu.cs` (966 lines) -- delete entirely
- `HotReloadHelper.cs` line 39 -- remove `"VillagerDialogMenu"` from `ModGameObjectNames`
- `Patches/DialogPatches.cs` lines 70, 87 -- remove `VillagerDialogMenu.IsVisible` guards
- `NPCs/AI/UI/VillagerBehaviorBridge.cs` line 54 -- update comment reference

**File splits:**
- `TaskQueue/Handlers/HnaPartitionHandler.cs` (737 lines) -> `HnaPartitionHandler.cs` + `HnaGridBuilder.cs`
- `NPCs/AI/Guards/HnaBoundaryMapper.cs` (641 lines) -> `HnaBoundaryMapper.cs` + `BoundaryGeometry.cs`
- `NPCs/AI/UI/Tabs/VillagerTabManager.cs` (634 lines) -> `VillagerTabManager.cs` + `VillagerTabRenderer.cs`
- `Items/VirtualRecipes/VirtualRecipeLoader.cs` (560 lines) -> `VirtualRecipeLoader.cs` + `VirtualRecipeParser.cs`
- `NPCs/AI/Navigation/HnaRegionGraph.cs` (523 lines) -> `HnaRegionGraph.cs` + `HnaGraphPersistence.cs`
- `NPCs/AI/VillagerAI.cs` (509 lines) -> `VillagerAI.cs` + `VillagerAISleep.cs`

**Behavior system refactor:**
- `NPCs/AI/Guards/GuardBehavior.cs` -- decompose into `Behaviors/Patrol/` and `Behaviors/Alarm/`
- `NPCs/AI/Guards/GuardPatrolDiscovery.cs` -- rename and move to `Behaviors/Patrol/PatrolDiscovery.cs`
- `NPCs/AI/Guards/GuardPatrolRefiner.cs` -- rename and move to `Behaviors/Patrol/PatrolRefiner.cs`
- `NPCs/AI/Guards/BreachDetection.cs` -- move to `Behaviors/Alarm/BreachDetection.cs`
- `NPCs/AI/VillagerAI.cs` -- refactor constructor and UpdateAI to use `BehaviorFactory`
- `NPCs/Definitions/*.json` -- add `behaviors` and `tags` arrays
- `NPCs/NpcTypeDefinition.cs` -- add `behaviors` and `tags` fields

**UI restructure:**
- `NPCs/AI/UI/` -- move entire folder to `UI/`
- `NPCs/AI/UI/Tabs/InfoTab.cs` -- extract guard-specific sections into `GuardStatusPanel`
- `NPCs/AI/UI/Tabs/DebugTab.cs` + `DebugTab.Guard.cs` -- extract map sections into `VillageMapPanel`
- `NPCs/AI/UI/VillagerInteract.cs` -- simplify to use `IBehavior.GetStatusText()`

## Design Reference

See `.cursor/plans/codebase_restructuring_plan_7fbea468.plan.md`, Phase 3 (lines 104-516).
