# Tasks: split-behavior-context

## Implementation Checklist

*Extract types from BehaviorContext.cs into focused files. No logic changes. Verify with `dotnet build`.*

### Step 1: Create BehaviorEnums.cs (~30 lines)

- [x] Create `NPCs/AI/BehaviorEnums.cs`
- [x] Move `TimeOfDay` enum from BehaviorContext.cs
- [x] Move `LocationType` enum from BehaviorContext.cs
- [x] Move `BehaviorState` enum from BehaviorContext.cs
- [x] Set namespace to `ValheimVillages.NPCs.AI`

### Step 2: Create KnownLocation.cs (~80 lines)

- [x] Create `NPCs/AI/KnownLocation.cs`
- [x] Move `KnownLocation` class from BehaviorContext.cs (including all methods: `IsSameLocation`, `IsTooCloseForSameType`, `GetMinDistanceForType`, `GetMaxLocationsForType`, `GetQualityScore`)
- [x] Set namespace to `ValheimVillages.NPCs.AI`

### Step 3: Create BehaviorSettings.cs (~120 lines)

- [x] Create `NPCs/AI/BehaviorSettings.cs`
- [x] Move `VillagerSettings` static class from BehaviorContext.cs
- [x] Move `DoorSettings` static class from BehaviorContext.cs
- [x] Move `ExplorationSettings` static class from BehaviorContext.cs
- [x] Move `WorkSettings` static class from BehaviorContext.cs
- [x] Set namespace to `ValheimVillages.NPCs.AI`

### Step 4: Trim BehaviorContext.cs (~20 lines)

- [x] Remove all extracted types from `BehaviorContext.cs`
- [x] Keep only the `BehaviorContext` struct (IsRaining, TimeOfDay, InShelter, CurrentComfort, CurrentPosition)
- [x] Verify the struct still references `TimeOfDay` from `BehaviorEnums.cs` (same namespace, no import needed)

### Step 5: Update Consuming Files

- [x] Identify all files importing types from the old BehaviorContext.cs (`rg "BehaviorState|TimeOfDay|LocationType|KnownLocation|VillagerSettings|DoorSettings|ExplorationSettings|WorkSettings" --type cs`)
- [x] For files using only enums: verify no import changes needed (same namespace)
- [x] For files using `KnownLocation` from outside `NPCs/AI/`: add `using ValheimVillages.NPCs.AI` if missing
- [x] For files using settings from outside `NPCs/AI/`: add `using ValheimVillages.NPCs.AI` if missing
- [x] Run `dotnet build` and fix any remaining import issues

## Notes

- All paths are relative to `src/ValheimVillages/`
- Since all 4 new files share the same namespace (`ValheimVillages.NPCs.AI`), most consuming files within `NPCs/AI/` will not need import changes
- Files outside `NPCs/AI/` that reference these types may need updated imports
- `KnownLocation` is a class (not struct) per the restructuring plan
