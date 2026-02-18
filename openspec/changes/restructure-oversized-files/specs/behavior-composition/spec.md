# behavior-composition - Spec Delta

## ADDED Requirements

### Requirement: Tag-Based Behavior Assignment

NPC behaviors SHALL be assigned via `behaviors` arrays in NPC JSON definitions, not hardcoded type checks in VillagerAI.

#### Scenario: Adding a new behavior to an NPC type

GIVEN a new behavior `"forage"` is implemented as an `IBehavior` class
WHEN a developer adds `"forage"` to an NPC type's `behaviors` array in JSON
THEN that NPC type uses the forage behavior at runtime without modifying VillagerAI

### Requirement: Priority-Based Behavior Dispatch

The `IBehavior` with the highest `Priority` that returns `true` from `WantsControl(context)` SHALL be the active behavior each frame.

#### Scenario: Alarm overrides patrol

GIVEN a guard NPC has behaviors `["patrol", "alarm"]`
AND the alarm behavior (priority 100) detects a breach
WHEN `WantsControl` is evaluated
THEN the alarm behavior takes control over patrol (priority 30)

### Requirement: File Size Limit

No source file SHALL exceed ~300 lines. Files exceeding this limit SHALL be split into focused subfiles.

#### Scenario: Oversized file is split

GIVEN `HnaPartitionHandler.cs` is 737 lines
WHEN the restructuring is applied
THEN it is split into `HnaPartitionHandler.cs` (~200 lines) and `HnaGridBuilder.cs` (~500 lines)

### Requirement: NPC Tag System

NPC definitions SHALL support a free-form `tags` array using `namespace:value` convention for capabilities beyond AI behaviors (e.g., `"teacher:ability"`, `"passive:spawnblock"`, `"tab:info"`).

#### Scenario: Tags drive UI visibility

GIVEN a mountaineer NPC has tag `"teacher:ability"`
WHEN a player interacts with the mountaineer
THEN the "Learn Ability" interaction option is shown based on the tag, not a hardcoded NpcType check
