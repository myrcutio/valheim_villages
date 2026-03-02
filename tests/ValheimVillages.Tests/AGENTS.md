# ValheimVillages.Tests

Keywords: test, unit test, xUnit, dotnet test, FloodFillTests, PathScoringTests, BoundaryPipelineTests, TabContractTests, AbilityContractTests, BehaviorContractTests, NpcDefinitionTests, TagParserTests, TaskQueueTests, contract test, algorithm test, definition test, integration test, regression test, NpcTypeDefinition, TagParser, net10.0

## Purpose

Unit tests for ValheimVillages. Runs via `dotnet test` without launching the game. Covers algorithms, registration contracts, NPC definitions, and task queue data types. Contract tests use safe reflection to skip types with unresolvable Unity dependencies.

## Directory Structure

```
ValheimVillages.Tests/
  Algorithms/
    FloodFillTests.cs                  -- BFS: single bed, empty beds, radius, slopes, barriers, Y tolerance
    PathScoringTests.cs                -- Identical paths, empty pipeline, nearby/far, segment vs point, combined
    BoundaryPipelineTests.cs           -- Chaikin, RDP, SortClockwise, DeduplicateByXZ, PruneSharpAngles, full
  Contracts/
    TabContractTests.cs                -- [RegisterTab] types implement IVillagerTab; Id not empty
    AbilityContractTests.cs            -- [RegisterAbility] types implement IAbility; Id not empty
    BehaviorContractTests.cs           -- [RegisterBehavior] types implement IBehavior; Tag not empty
  Definitions/
    NpcDefinitionTests.cs              -- NpcTypeDefinition parsing, GetNpcType, GetCategory, tag filtering
    TagParserTests.cs                  -- TryParse, FilterByNamespace, GetValues, HasTag, case insensitivity
  TaskQueue/
    TaskQueueTests.cs                  -- TaskPriority, TaskResult.Ok/Fail, VillagerTask, behavior priorities
```

## Test Categories

| Category | What it verifies |
|----------|-----------------|
| **Algorithms** | FloodFill BFS, PathScoring metrics, BoundaryPipeline geometry transforms |
| **Contracts** | All `[Register*]` attributed types implement their required interface |
| **Definitions** | NPC JSON definitions parse correctly; TagParser handles all formats |
| **TaskQueue** | Task data types serialize/compare correctly; priority ordering |

## Running Tests

```bash
dotnet test tests/ValheimVillages.Tests/
```

References the main `ValheimVillages` project. Contract tests use `AssemblyHelper` to safely reflect over types that may depend on unavailable Unity assemblies.

## Integration

- **ValheimVillages** -- the only project reference; tests algorithms, schemas, and registration attributes.
- Contract tests scan the assembly for `[Register*]` attributes to verify interface compliance, skipping types whose metadata can't be loaded outside the game runtime.
- Adding a new `[RegisterTab]`, `[RegisterAbility]`, or `[RegisterBehavior]` type will be caught by contract tests if it fails to implement the required interface.
