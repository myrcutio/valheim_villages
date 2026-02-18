# Change Proposal: colocate-feature-patches

## Intent

Move patch files from the centralized `Patches/` directory to live alongside the feature code they modify. An agent working on a feature should find all related code -- including Harmony patches -- in one folder, reducing cross-directory navigation and context window waste.

## Scope

- Move 15 patch files from `Patches/`, `Patches/Fragments/`, and `Patches/WorkOrders/` to their feature directories
- Update namespaces in each moved file to match the new location
- Leave 4 cross-cutting patches (ItemPatch, PrefabProtectionPatch, LocalizationPatch, DiagnosticPatch) in `Patches/`
- No logic changes -- purely structural
- Out of scope: File splits, new interfaces, behavior changes

## Affected Specs

None. This is a structural change with no behavioral impact.

## Existing Code to Modify

All paths relative to `src/ValheimVillages/`:

- `Patches/Fragments/FragmentLootPatch.cs` -- namespace update
- `Patches/Fragments/FragmentUsePatch.cs` -- namespace update
- `Patches/Fragments/RescueQuestProximityPatch.cs` -- namespace update
- `Patches/WorkOrders/CraftingStationPatch.cs` -- namespace update
- `Patches/WorkOrders/WorkOrderUsePatch.cs` -- namespace update
- `Patches/WorkOrders/WorkOrderIconPatch.cs` -- namespace update
- `Patches/WorkOrders/WorkOrderTutorial.cs` -- namespace update
- `Patches/WorkOrders/RecipeHelper.cs` -- namespace update
- `Patches/SpawnProtectionPatch.cs` -- namespace update, rename to SpawnBlockPassive.cs
- `Patches/EnemyAvoidancePatch.cs` -- namespace update
- `Patches/MountaineerPatches.cs` -- namespace update
- `Patches/VillagerPawnPatch.cs` -- namespace update
- `Patches/DialogPatches.cs` -- namespace update
- `Patches/VillagerCraftingPatch.cs` -- namespace update
- `Patches/VillagerAIPatch.cs` -- namespace update

## Design Reference

See `.cursor/plans/codebase_restructuring_plan_7fbea468.plan.md`, Phase 1 (lines 32-66).

## Verification

`dotnet build` should pass with zero errors after all moves and namespace updates.
