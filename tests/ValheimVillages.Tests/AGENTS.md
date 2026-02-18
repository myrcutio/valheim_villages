# ValheimVillages.Tests

Keywords: test, unit test, xUnit, dotnet test, FloodFillTests, PathScoringTests, BoundaryPipelineTests, TabContractTests, AbilityContractTests, BehaviorContractTests, NpcDefinitionTests, TagParserTests, TaskQueueTests, contract test, algorithm test, definition test, integration test, regression test, NpcTypeDefinition, TagParser, net10.0

## Purpose

Unit tests for ValheimVillages.Core. Runs via `dotnet test` with no Unity or Valheim dependencies. Covers algorithms, registration contracts, NPC definitions, and task queue data types.

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

No game installation or Unity runtime required. References only `ValheimVillages.Core`.

## Integration

- **ValheimVillages.Core** -- the only project reference; tests Core algorithms, data, and attributes.
- Contract tests scan the Core assembly for `[Register*]` attributes to verify interface compliance.
- Adding a new `[RegisterTab]`, `[RegisterAbility]`, or `[RegisterBehavior]` type in the mod will be caught by contract tests if it fails to implement the required interface.
