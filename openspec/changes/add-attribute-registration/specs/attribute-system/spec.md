# attribute-system - Spec Delta

## ADDED Requirements

### Requirement: Attribute-Based Auto-Registration

Adding a C# attribute to a method or class SHALL automatically register it with the appropriate system. No central file SHALL need editing to add new registrations.

#### Scenario: New console command via attribute

GIVEN a developer creates a static method `ToggleMarkers()` with `[DevCommand("Toggle HNA debug markers")]`
WHEN `AttributeScanner.ScanAndRegister()` runs at plugin startup
THEN `"hnadebugvisualization togglemarkers"` is registered as a console command

### Requirement: Ability Self-Registration

Ability implementations SHALL self-register via `[RegisterAbility("id")]` and implement `IAbility`. NPC tags like `"ability:mountainstride"` SHALL resolve to the registered ability at interaction time.

#### Scenario: Mountain Stride ability lookup

GIVEN `MountainStrideAbility` is annotated with `[RegisterAbility("mountainstride")]`
AND a mountaineer NPC has tag `"ability:mountainstride"`
WHEN a player interacts with the mountaineer
THEN `AttributeScanner.GetAbility("mountainstride")` returns the `MountainStrideAbility` instance

### Requirement: Passive Effect Self-Registration

Passive effects SHALL self-register via `[RegisterPassive("id")]` and implement `IPassiveEffect`. NPC tags like `"passive:spawnblock"` SHALL activate the effect for that NPC.

#### Scenario: Spawn block passive lookup

GIVEN `SpawnBlockPassive` is annotated with `[RegisterPassive("spawnblock")]`
AND a guard NPC has tag `"passive:spawnblock"`
WHEN the spawn system checks for protection
THEN the passive registry provides `SpawnBlockPassive.IsActive(position)`
