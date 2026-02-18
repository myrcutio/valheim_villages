# Change Proposal: add-attribute-registration

## Intent

Create a decorator-style annotation system where adding a C# attribute to a method or class automatically registers it with the right system -- console commands, UI rows, ObjectDB entries, task handlers, cleanup hooks, behaviors, abilities, passives, and UI components. New features just add an attribute; no central file needs editing.

## Scope

- Create 14 attribute types (13 registration + `[ModTest]`)
- Create `AttributeScanner` (~100 lines) to discover and register all annotated types/methods
- Create shared `UIHelpers` (~40 lines)
- Create `IAbility` and `IPassiveEffect` interfaces
- Refactor `MountainStrideAbility` to implement `IAbility` with `[RegisterAbility("mountainstride")]`
- Refactor `SpawnBlockPassive` to implement `IPassiveEffect` with `[RegisterPassive("spawnblock")]`
- Annotate existing console commands, debug rows, info rows, task handlers, cleanup hooks, and ObjectDB registrations
- Replace ~500+ lines of manual registration across 6+ files with ~450 lines of new infrastructure
- Out of scope: Assembly split (Phase 6), HotReloadHelper simplification (Phase 5 -- depends on this phase)

## Affected Specs

None. Registration is an internal mechanism change; external behavior is preserved.

## Existing Code to Modify

All paths relative to `src/ValheimVillages/`:

**Console commands to annotate (replaces 11 manual `Terminal.ConsoleCommand` calls):**
- `NPCs/AI/Navigation/HnaDebugVisualization.cs` -- `ToggleMarkers()` gets `[DevCommand]`
- `NPCs/AI/Navigation/HnaBoundaryDump.cs` -- `Dump()` gets `[DevCommand]`
- `Abilities/VillagerAbilityManager.cs` -- multiple commands get `[DevCommand]`

**Task handlers to annotate (replaces 8 manual `Register()` calls):**
- All `ITaskHandler` implementations in `TaskQueue/Handlers/` get `[RegisterTaskHandler]`

**Cleanup hooks to annotate (replaces 9 manual calls in HotReloadHelper):**
- `NPCs/AI/VillagerAIManager.cs` -- `Clear()` gets `[RegisterCleanup]`
- `TaskQueue/GlobalTaskQueue.cs` -- `Clear()` gets `[RegisterCleanup]`
- Other static state holders get `[RegisterCleanup]`

**ObjectDB registrations to annotate:**
- `Abilities/MountainStride/SE_MountainStride.cs` -- `Register()` gets `[RegisterObjectDB]`
- `Plugin.cs` line 84 -- manual registration replaced by scanner invocation

**Ability refactor:**
- `Abilities/VillagerAbilityManager.cs` -- hardcoded MountainStride methods replaced by `IAbility` interface
- `UI/Tabs/InfoTab.cs` -- `NpcType.Mountaineer` checks (lines 99, 113) replaced by tag query
- `UI/Tabs/InfoTab.cs` -- `GetMountaineerDetail()` (lines 159-180) replaced by generic `GetAbilityDetail(IAbility)`

**Passive refactor:**
- `Abilities/SpawnBlock/SpawnBlockPassive.cs` -- implement `IPassiveEffect`
- `Villages/SpawnProtectionPatch.cs` -- delegate to `IPassiveEffect.IsActive()` via registry

**Mod object registration:**
- `HotReloadHelper.cs` -- `ModGameObjectNames` array replaced by `[RegisterModObject]` on MonoBehaviour classes

## Design Reference

See `.cursor/plans/codebase_restructuring_plan_7fbea468.plan.md`, Phase 4 (lines 519-871).
