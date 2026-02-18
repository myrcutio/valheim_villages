# Tasks: split-core-assembly

## Implementation Checklist

*Create new projects, move types, bridge vectors, write tests, build test runner. Verify with `dotnet build` and `dotnet test` throughout.*

### Sub-step 6a: Create ValheimVillages.Core Project

- [ ] Create `src/ValheimVillages.Core/ValheimVillages.Core.csproj` (netstandard2.0, no dependencies)
- [ ] Create directory structure: `Interfaces/`, `Attributes/`, `Enums/`, `Data/`, `Tags/`, `Algorithms/`

**Move interfaces to Core:**
- [ ] Move `IBehavior.cs` to `src/ValheimVillages.Core/Interfaces/IBehavior.cs`
- [ ] Move `IAbility.cs` to `src/ValheimVillages.Core/Interfaces/IAbility.cs`
- [ ] Move `IPassiveEffect.cs` to `src/ValheimVillages.Core/Interfaces/IPassiveEffect.cs`
- [ ] Move `IVillagerTab.cs` to `src/ValheimVillages.Core/Interfaces/IVillagerTab.cs`
- [ ] Move `IListPanel.cs` to `src/ValheimVillages.Core/Interfaces/IListPanel.cs`
- [ ] Move `IContextMenu.cs` to `src/ValheimVillages.Core/Interfaces/IContextMenu.cs`
- [ ] Move `ITaskHandler.cs` to `src/ValheimVillages.Core/Interfaces/ITaskHandler.cs`

**Move attribute definitions to Core:**
- [ ] Move all 14 attribute classes from `src/ValheimVillages/Core/Attributes/` to `src/ValheimVillages.Core/Attributes/`
- [ ] Move `DebugAction.cs` to `src/ValheimVillages.Core/Attributes/DebugAction.cs`

**Move enums to Core:**
- [ ] Move `BehaviorState`, `LocationType`, `TimeOfDay` from `BehaviorEnums.cs` to `src/ValheimVillages.Core/Enums/`
- [ ] Move `NpcType` to `src/ValheimVillages.Core/Enums/NpcType.cs`
- [ ] Move `TaskPriority` to `src/ValheimVillages.Core/Enums/TaskPriority.cs`

**Move data types to Core:**
- [ ] Move `NpcTypeDefinition` to `src/ValheimVillages.Core/Data/NpcTypeDefinition.cs`
- [ ] Move `VillagerTask` to `src/ValheimVillages.Core/Data/VillagerTask.cs`
- [ ] Move `TaskSettings` to `src/ValheimVillages.Core/Data/TaskSettings.cs`
- [ ] Move `KnownLocation` to `src/ValheimVillages.Core/Data/KnownLocation.cs`
- [ ] Move `BehaviorContext` to `src/ValheimVillages.Core/Data/BehaviorContext.cs`
- [ ] Move `TabListItem` to `src/ValheimVillages.Core/Data/TabListItem.cs`
- [ ] Move `TabDetailData` to `src/ValheimVillages.Core/Data/TabDetailData.cs`

**Create tag system in Core:**
- [ ] Create `src/ValheimVillages.Core/Tags/TagParser.cs` -- `namespace:value` parsing, matching, filter utilities

**Move pure algorithms to Core:**
- [ ] Extract BoundaryPipeline (RDP, Chaikin, clockwise sort, dedup) to `src/ValheimVillages.Core/Algorithms/BoundaryPipeline.cs`
- [ ] Extract FloodFill (HNA region BFS) to `src/ValheimVillages.Core/Algorithms/FloodFill.cs`
- [ ] Extract PathScoring to `src/ValheimVillages.Core/Algorithms/PathScoring.cs`

- [ ] Add `<ProjectReference>` to `src/ValheimVillages/ValheimVillages.csproj` referencing `ValheimVillages.Core`
- [ ] Update all `using` statements in the mod assembly for moved types
- [ ] Run `dotnet build`

### Sub-step 6b: Vec3 Bridge

- [ ] Create `src/ValheimVillages.Core/Vec3.cs` -- lightweight readonly struct with `x, y, z`, `DistanceTo()`, `DistXZ()`
- [ ] Create `src/ValheimVillages/Vec3Extensions.cs` -- `ToVec3(this Vector3)`, `ToVector3(this Vec3)`
- [ ] Update `KnownLocation.Position` from `Vector3` to `Vec3`
- [ ] Update `BehaviorContext.CurrentPosition` from `Vector3` to `Vec3`
- [ ] Add `.ToVec3()` calls at mod boundaries where `Vector3` is converted to Core types (e.g., `villager.transform.position.ToVec3()`)
- [ ] Run `dotnet build`

### Sub-step 6c: Create ValheimVillages.Tests Project (xUnit)

- [ ] Create `tests/ValheimVillages.Tests/ValheimVillages.Tests.csproj` (net8.0, xUnit, references Core)
- [ ] Add xUnit package references: `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk`
- [ ] Create directory structure: `Contracts/`, `Definitions/`, `Algorithms/`, `TaskQueue/`

**Contract tests:**
- [ ] Create `tests/ValheimVillages.Tests/Contracts/BehaviorContractTests.cs` -- verify `[RegisterBehavior]` classes implement `IBehavior`
- [ ] Create `tests/ValheimVillages.Tests/Contracts/AbilityContractTests.cs` -- verify `[RegisterAbility]` classes implement `IAbility`
- [ ] Create `tests/ValheimVillages.Tests/Contracts/TabContractTests.cs` -- verify `[RegisterTab]` classes implement `IVillagerTab`

**NPC definition tests:**
- [ ] Create `tests/ValheimVillages.Tests/Definitions/NpcDefinitionTests.cs` -- validate NPC JSON tag references match registered components
- [ ] Create `tests/ValheimVillages.Tests/Definitions/TagParserTests.cs` -- test `namespace:value` parsing, filtering, matching

**Algorithm tests (migrated from HnaPartitionTests):**
- [ ] Create `tests/ValheimVillages.Tests/Algorithms/BoundaryPipelineTests.cs` -- Chaikin smoothing, RDP simplification, clockwise sort, dedup
- [ ] Create `tests/ValheimVillages.Tests/Algorithms/FloodFillTests.cs` -- barrier handling, region isolation
- [ ] Create `tests/ValheimVillages.Tests/Algorithms/PathScoringTests.cs` -- scoring logic

**Task queue tests:**
- [ ] Create `tests/ValheimVillages.Tests/TaskQueue/TaskQueueTests.cs` -- priority ordering, dedup, retry, dead letter

**Interface boundary tests:**
- [ ] Add test: `BehaviorPriorities_AreUnique_AcrossAllRegistered`

- [ ] Run `dotnet test tests/ValheimVillages.Tests/`
- [ ] Delete `tests/HnaPartitionTests/` project after all tests migrated

### Sub-step 6d: Solution File

- [ ] Create `ValheimVillages.sln` at repo root linking:
  - `src/ValheimVillages.Core/ValheimVillages.Core.csproj`
  - `src/ValheimVillages/ValheimVillages.csproj`
  - `tests/ValheimVillages.Tests/ValheimVillages.Tests.csproj`
- [ ] Verify `dotnet build ValheimVillages.sln` succeeds
- [ ] Verify `dotnet test ValheimVillages.sln` runs all unit tests

### Sub-step 6e: In-Game Integration Tests ([ModTest])

**Test runner:**
- [ ] Create `src/ValheimVillages/Core/Testing/ModTestRunner.cs` (~120 lines)
  - `RunAll()` -- discovers `[ModTest]` methods via `AttributeScanner.GetModTests()`, runs them, writes JSON results
  - `AutoRunEnabled` config toggle (default true)
  - Log line counting before/after test run for `logRef.lines`
  - Per-test log line tracking for `logLines`
  - JSON output to `BepInEx/config/vv_test_results.json` (schema v1, see `.cursor/plans/json_test_output_format_c3f4db7f.plan.md`)

**Assert helpers:**
- [ ] Create `src/ValheimVillages/Core/Testing/ModAssert.cs`
  - `True(bool condition, string message)` -- throws `ModAssertException`
  - `Equal<T>(T expected, T actual, string message)` -- captures expected/actual in exception
  - `NotNull(object obj, string message)`
  - `Collect()` -- soft-assert mode for multiple assertions per test
- [ ] Create `ModAssertException` with `Assertions` list containing `{message, expected, actual}` entries

**Source location extraction:**
- [ ] Implement `BuildResult(MethodInfo method, Exception ex, int logStart, int logEnd)` in ModTestRunner
  - Parse `Exception.StackTrace` for `in <file>:line <n>` patterns (PDB-backed)
  - Extract repo-relative file path, line number, fully qualified class name
  - Fallback to `class + method` name if no PDB info

**JSON results writer:**
- [ ] Implement `WriteJsonResults()` in ModTestRunner
  - Build JSON manually via StringBuilder (no library dependency)
  - Schema: `v`, `ts`, `result`, `summary`, `logRef`, `failures[]`, `errors[]`, `skipped[]`
  - Each failure: `test`, `state`, `location {file, line, class}`, `message`, `assertions[]`, `stackTrace`, `logLines`
  - See `.cursor/plans/json_test_output_format_c3f4db7f.plan.md` for full schema reference

**Console commands:**
- [ ] Register `vv_runtests` console command (or annotate with `[DevCommand]`)

**Example integration tests:**
- [ ] Create patrol integration test: `Behaviors/Patrol/PatrolIntegrationTests.cs` -- verify patrol villagers have waypoints
- [ ] Create spawn block test: `Abilities/SpawnBlock/SpawnBlockTests.cs` -- verify spawn block active in village areas
- [ ] Create tab tag resolution test: `UI/Tabs/TabIntegrationTests.cs` -- verify all NPC tab tags resolve to registered tabs

**Wire auto-run:**
- [ ] Add `ModTestRunner.RunAll()` call in `Plugin.Awake()` after hot-reload cleanup (when `AutoRunEnabled` is true)
- [ ] Run `dotnet build`

### Sub-step 6f: Scene Snapshot Utility

- [ ] Create `src/ValheimVillages/Core/Testing/SceneSnapshot.cs`
  - `VillagerSnapshot` class: `UniqueId`, `NpcType`, `BehaviorState`, `Position[3]`, `ActiveBehaviorTags[]`, `KnownLocationCount`
  - `VillageAreaSnapshot` class: area data
  - `SceneSnapshot.Capture()` -- captures current game state
  - `SceneSnapshot.SaveToFile(path)` -- writes to JSON
  - `SceneSnapshot.LoadFromFile(path)` -- reads from JSON
- [ ] Register `vv_snapshot` console command -- captures state to `BepInEx/config/vv_snapshot.json`
- [ ] Register `vv_snapshot_verify` console command -- compares current state against saved snapshot, reports diffs
- [ ] Run `dotnet build`

### Sub-step 6g: Agent-Ready Test Loop Setup

**One-time configuration:**
- [ ] Document ScriptEngine FileSystemWatcher config in `BepInEx/config/com.bepis.bepinex.scriptengine.cfg`:
  ```ini
  [AutoReload]
  EnableFileSystemWatcher = true
  AutoReloadDelay = 3
  ```

**Cursor rule:**
- [ ] Create `.cursor/rules/test-loop.mdc` with agent workflow:
  1. `dotnet build` + `dotnet test tests/ValheimVillages.Tests/`
  2. Wait 6s for ScriptEngine auto-reload
  3. Read `vv_test_results.json` -- if `"result": "PASS"` done; if `"FAIL"` parse `failures`/`errors`
  4. For failures: use `location.file`/`location.line`, `assertions[].expected`/`actual`, `stackTrace`, `logLines`
  5. Fallback: `tail -30 LogOutput.log` if JSON missing or stale

- [ ] Verify full loop: edit -> `dotnet build` -> `dotnet test` -> auto-reload (3s) -> `ModTestRunner.RunAll()` -> read `vv_test_results.json`

## Dependencies

- Phases 1-5 must be complete (interfaces, attributes, behaviors, and UI components exist and are annotated)
- Sub-step 6a (Core project) must be first
- Sub-step 6b (Vec3) should follow 6a
- Sub-step 6c (Tests) can parallel with 6b
- Sub-step 6d (solution) after 6a and 6c
- Sub-step 6e (ModTest runner) after 6a
- Sub-steps 6f and 6g can be done last

## Notes

- All mod assembly paths are relative to `src/ValheimVillages/`
- Core assembly uses `Vec3` struct instead of `UnityEngine.Vector3`
- `TabDetailData.MapTexture` stays in mod assembly (Unity `Texture2D` type) -- Core's `TabDetailData` omits it
- Test results JSON schema documented in `.cursor/plans/json_test_output_format_c3f4db7f.plan.md`
- Two-tier testing: Unit tests (xUnit, `dotnet test`, no Valheim needed) + Integration tests (`[ModTest]`, in-game, needs Valheim running)
- Full agent loop cycle: ~8-10 seconds from code edit to integration test feedback
