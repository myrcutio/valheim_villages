# Change Proposal: simplify-hotreload-helper

## Intent

With `[RegisterCleanup]` and `[RegisterModObject]` from Phase 4, HotReloadHelper can replace its manually maintained lists with single-line scanner calls. This shrinks the file from 320 lines to ~150 lines and eliminates the need to update HotReloadHelper when adding new modules.

## Scope

- Replace `ResetAllStaticState()` body (9 explicit calls) with `AttributeScanner.InvokeAllCleanup()`
- Replace `DestroyOrphanedModObjects()` body (hardcoded name array) with `AttributeScanner.GetModObjectNames()`
- Delete the `ModGameObjectNames` array
- Keep assembly-level reflection sweep, NPC fixup logic, and hot-reload detection (can't be attribute-driven)
- Out of scope: Attribute creation (Phase 4), assembly split (Phase 6)

## Affected Specs

None. Hot-reload behavior is preserved; only the implementation mechanism changes.

## Existing Code to Modify

All paths relative to `src/ValheimVillages/`:

- `HotReloadHelper.cs` (320 lines -> ~150 lines)
  - `ResetAllStaticState()` -- replace 9 explicit cleanup calls with 1 line
  - `DestroyOrphanedModObjects()` -- replace hardcoded array with scanner query
  - `ModGameObjectNames` array -- delete entirely

## Design Reference

See `.cursor/plans/codebase_restructuring_plan_7fbea468.plan.md`, Phase 5 (lines 875-914).

## Verification

`dotnet build` should pass. Hot-reload in Valheim should still clean up stale objects and restore state correctly.
