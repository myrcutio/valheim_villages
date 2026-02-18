# Change Proposal: split-core-assembly

## Intent

Extract Unity-independent logic into a separate `ValheimVillages.Core` assembly (netstandard2.0) that both the mod and a proper test project can reference. This eliminates the current pattern of duplicating algorithms in test code and enables comprehensive unit testing, in-game integration testing, and an agent-ready automated test loop.

## Scope

- Create `src/ValheimVillages.Core/` project (netstandard2.0, zero Unity/Valheim deps)
- Move all interfaces, attributes, enums, data types, tag system, and pure algorithms to Core
- Create `Vec3` struct in Core and `Vec3Extensions.cs` bridge in the mod
- Create `tests/ValheimVillages.Tests/` project (net8.0, xUnit, references Core)
- Migrate algorithm tests from `tests/HnaPartitionTests/` and delete that project
- Add contract tests, NPC definition validation, tag system tests
- Create `[ModTest]` in-game integration test runner with JSON output (`vv_test_results.json`)
- Create `ModAssert` with expected/actual capture and `ModAssertException`
- Create `SceneSnapshot` for regression testing
- Create `ValheimVillages.sln` linking all 3 projects
- Create `.cursor/rules/test-loop.mdc` cursor rule for agent workflow
- Out of scope: Behavioral changes, UI changes, new game features

## Affected Specs

None. This is an infrastructure change enabling testability.

## Existing Code to Modify

All paths relative to project root:

**Types moving to Core (from `src/ValheimVillages/`):**
- All interfaces: `IBehavior`, `IAbility`, `IPassiveEffect`, `IVillagerTab`, `IListPanel`, `IContextMenu`, `ITaskHandler`
- All 14 attribute definitions from `Core/Attributes/`
- Enums: `BehaviorState`, `LocationType`, `TimeOfDay`, `NpcType`, `NpcCategory`, `TaskPriority`
- Data types: `NpcTypeDefinition`, `VillagerTask`, `TaskSettings`, `KnownLocation`, `BehaviorContext`, `TabListItem`, `TabDetailData`, `DebugAction`
- Tag system: tag parser, matching utilities
- Pure algorithms: BoundaryPipeline, FloodFill, PathScoring

**Types staying in mod (depend on Unity/Valheim):**
- `Plugin.cs`, all MonoBehaviours
- Harmony patches
- `AttributeScanner` (creates instances, stays in mod)
- Unity UI code (VillagerTabManager, VillagerUIFactory, etc.)
- ZDO read/write code

**Vec3 bridge:**
- `KnownLocation.Position` changes from `Vector3` to `Vec3`
- `BehaviorContext.CurrentPosition` changes from `Vector3` to `Vec3`
- New `src/ValheimVillages/Vec3Extensions.cs` provides `ToVec3()` / `ToVector3()` conversions

**Test migration:**
- `tests/HnaPartitionTests/` -- delete after migrating algorithms and test data

## Design Reference

See `.cursor/plans/codebase_restructuring_plan_7fbea468.plan.md`, Phase 6 (lines 917-1563).
See `.cursor/plans/json_test_output_format_c3f4db7f.plan.md` for test results JSON schema.
