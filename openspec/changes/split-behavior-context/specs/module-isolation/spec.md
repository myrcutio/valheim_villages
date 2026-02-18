# module-isolation - Spec Delta

## ADDED Requirements

### Requirement: BehaviorContext Type Separation

Types in `NPCs/AI/BehaviorContext.cs` SHALL be split by category so that consuming modules import only the types they need.

- Enums (`TimeOfDay`, `LocationType`, `BehaviorState`) SHALL be in `BehaviorEnums.cs`
- The `KnownLocation` class SHALL be in `KnownLocation.cs`
- The `BehaviorContext` struct SHALL remain in `BehaviorContext.cs`
- Static settings classes SHALL be in `BehaviorSettings.cs`

#### Scenario: Module imports only needed types

GIVEN a file only needs the `BehaviorState` enum
WHEN it imports from `NPCs/AI/`
THEN it does not also import `WorkSettings`, `KnownLocation`, or other unrelated types into its compilation unit
