# Tasks: simplify-hotreload-helper

## Implementation Checklist

*Mostly deletion. Depends on Phase 4 ([RegisterCleanup] and [RegisterModObject] already created and wired). Verify with `dotnet build`.*

### Step 1: Simplify ResetAllStaticState

- [x] Replace body of `ResetAllStaticState()` with single call: `AttributeScanner.InvokeAllCleanup(CurrentAssembly);`
- [x] Verify all 9 previously explicit cleanup calls are now covered by `[RegisterCleanup]` attributes on their respective classes:
  - `VillagerAIManager.Clear()`
  - `VillagerRestoration.ClearTracking()`
  - `GlobalTaskQueue.Clear()`
  - `VillageAreaManager.Clear()`
  - `HnaDebugVisualization.ClearMarkers()`
  - `HnaRegionGraph.Clear()`
  - `RescueQuestTracker.Clear()`
  - `VillagerActivityLog.ResetInstance()`
  - `CraftingStationPatch.Clear()`
- [x] Delete the 9 explicit cleanup call lines

### Step 2: Simplify DestroyOrphanedModObjects

- [x] Replace `ModGameObjectNames.Contains(name)` check with `AttributeScanner.GetModObjectNames(CurrentAssembly).Contains(name)` (kept existing prefix-based detection intact)
- [x] Verify all entries in `ModGameObjectNames` are now covered by `[RegisterModObject]` attributes on their MonoBehaviour classes:
  - `VillagerTabManager` -- `[RegisterModObject("VillagerTabManager")]` on `UI/Core/VillagerTabManager.cs`
  - `WorkOrderMenu` -- `[RegisterModObject("WorkOrderMenu")]` on `UI/ContextMenus/WorkOrderMenu.cs`
- [x] Delete the `ModGameObjectNames` string array

### Step 3: Clean Up Unused Imports

- [x] Remove `using` statements that are no longer needed after deleting manual cleanup/object lists (removed 6 imports)
- [x] Run `dotnet build` -- 0 warnings, 0 errors

### Step 4: Verify Remaining Code

- [x] Confirm the following remain in HotReloadHelper (cannot be attribute-driven):
  - `DestroyStaleComponents()` -- assembly-level reflection sweep
  - `FixupExistingNPCs()` -- ZNetView iteration + VillagerRestoration
  - Hot-reload detection logic (checking CurrentAssembly vs loaded assemblies)
- [x] Verify file size: 319 -> 292 lines (proposal estimated ~150, but remaining methods are non-trivial and cannot be simplified further)
- [x] Run `dotnet build` -- 0 warnings, 0 errors

### Step 5: Integration Test

- [ ] Hot-reload in Valheim and verify:
  - Stale MonoBehaviours are destroyed
  - Mod GameObjects are cleaned up
  - Static state is reset
  - NPC fixup runs correctly
  - No orphaned objects remain

## Dependencies

- Phase 4 must be complete (specifically sub-steps 4g and 4h)

## Notes

- All paths are relative to `src/ValheimVillages/`
- Net reduction: ~170 lines deleted from HotReloadHelper
- The main risk is missing a cleanup call during the transition -- Step 1's verification catches this
