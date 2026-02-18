# Change Proposal: split-behavior-context

## Intent

Split `BehaviorContext.cs` (276 lines, imported by 15+ files) into 4 focused files so that modules only import what they need. An agent working on guard behavior should not need to load farming settings into context.

## Scope

- Split `NPCs/AI/BehaviorContext.cs` into 4 files by category
- Update `using` statements in ~15 consuming files
- No logic changes -- purely structural
- Out of scope: Any behavioral modifications, new interfaces, new systems

## Affected Specs

None. This is a structural change with no behavioral impact.

## Existing Code to Modify

All paths relative to `src/ValheimVillages/`:

- `NPCs/AI/BehaviorContext.cs` (276 lines) -- split into 4 files
- ~15 files that `using` BehaviorContext types -- update imports

## Design Reference

See `.cursor/plans/codebase_restructuring_plan_7fbea468.plan.md`, Phase 2 (lines 69-101).

## Verification

`dotnet build` should pass with zero errors after the split and import updates.
