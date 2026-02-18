# Tasks: colocate-feature-patches

## Implementation Checklist

*Pure file moves + namespace updates. No logic changes. Verify with `dotnet build` after each group.*

### Group 1: Fragment Patches -> Items/Fragments/

- [x] Move `Patches/Fragments/FragmentLootPatch.cs` to `Items/Fragments/FragmentLootPatch.cs`
- [x] Move `Patches/Fragments/FragmentUsePatch.cs` to `Items/Fragments/FragmentUsePatch.cs`
- [x] Move `Patches/Fragments/RescueQuestProximityPatch.cs` to `Items/Fragments/RescueQuestProximityPatch.cs`
- [x] Update namespace in each file from `ValheimVillages.Patches.Fragments` to `ValheimVillages.Items.Fragments`
- [x] Delete empty `Patches/Fragments/` directory

### Group 2: WorkOrder Patches -> Items/WorkOrders/ and Items/Icons/

- [x] Move `Patches/WorkOrders/CraftingStationPatch.cs` to `Items/WorkOrders/CraftingStationPatch.cs`
- [x] Move `Patches/WorkOrders/WorkOrderUsePatch.cs` to `Items/WorkOrders/WorkOrderUsePatch.cs`
- [x] Move `Patches/WorkOrders/WorkOrderTutorial.cs` to `Items/WorkOrders/WorkOrderTutorial.cs`
- [x] Move `Patches/WorkOrders/RecipeHelper.cs` to `Items/WorkOrders/RecipeHelper.cs`
- [x] Move `Patches/WorkOrders/WorkOrderIconPatch.cs` to `Items/Icons/WorkOrderIconPatch.cs`
- [x] Update namespaces: `ValheimVillages.Patches.WorkOrders` to `ValheimVillages.Items.WorkOrders` (or `ValheimVillages.Items.Icons` for the icon patch)
- [x] Delete empty `Patches/WorkOrders/` directory

### Group 3: Ability-Related Patches -> Abilities/

- [x] Move `Patches/SpawnProtectionPatch.cs` to `Abilities/SpawnBlock/SpawnBlockPassive.cs`
- [x] Update namespace to `ValheimVillages.Abilities.SpawnBlock`
- [x] Move `Patches/MountaineerPatches.cs` to `Abilities/MountainStride/MountaineerPatches.cs`
- [x] Update namespace to `ValheimVillages.Abilities.MountainStride`

### Group 4: Village and NPC Patches -> Feature Directories

- [x] Move `Patches/EnemyAvoidancePatch.cs` to `Villages/EnemyAvoidancePatch.cs`
- [x] Update namespace to `ValheimVillages.Villages`
- [x] Move `Patches/VillagerPawnPatch.cs` to `NPCs/VillagerPawnPatch.cs`
- [x] Update namespace to `ValheimVillages.NPCs`
- [x] Move `Patches/VillagerAIPatch.cs` to `NPCs/AI/VillagerAIPatch.cs`
- [x] Update namespace to `ValheimVillages.NPCs.AI`

### Group 5: UI Patches -> UI/Patches/

- [x] Create `UI/Patches/` directory
- [x] Move `Patches/DialogPatches.cs` to `UI/Patches/DialogPatches.cs`
- [x] Move `Patches/VillagerCraftingPatch.cs` to `UI/Patches/VillagerCraftingPatch.cs`
- [x] Update namespaces to `ValheimVillages.UI.Patches`

### Group 6: Fix References and Verify

- [x] Search all `.cs` files for `using ValheimVillages.Patches.Fragments` and update
- [x] Search all `.cs` files for `using ValheimVillages.Patches.WorkOrders` and update
- [x] Search all `.cs` files for `using ValheimVillages.Patches` and verify remaining references are to the 4 cross-cutting patches
- [x] Run `dotnet build` and fix any compilation errors
- [x] Verify the 4 cross-cutting patches remain in `Patches/`: ItemPatch.cs, PrefabProtectionPatch.cs, LocalizationPatch.cs, DiagnosticPatch.cs

## Notes

- All paths are relative to `src/ValheimVillages/`
- Groups can be done sequentially; build after each group to catch issues early
- No logic changes in any file -- only namespace declarations and using statements
