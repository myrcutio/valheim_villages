# npc-placement-assignment - Spec Delta

## ADDED Requirements

### Requirement: NPC item representation
+ The system SHALL represent NPCs as items that can be carried in player inventory or back slot.
+ The system SHALL allow NPCs to be placed in player quickslots (1-9).
+ The system SHALL track NPC state when carried as an item.

#### Scenario: Player acquires NPC as item
+ GIVEN a player has acquired an NPC (through rescue quest or other means)
+ WHEN the NPC is acquired
+ THEN the NPC is represented as an item (e.g., "swaddled villager")
+ AND the item can be placed in player inventory or back slot
+ AND the item can be assigned to a quickslot (1-9)

#### Scenario: Player places NPC in quickslot
+ GIVEN a player has an NPC item in inventory
+ WHEN the player assigns the NPC item to a quickslot
+ THEN the NPC item is available in the quickslot
+ AND the player can use the quickslot to interact with the NPC item

### Requirement: NPC placement on structures
+ The system SHALL allow players to use NPC items from quickslots on beds to assign NPCs to beds.
+ The system SHALL allow players to use NPC items from quickslots on workbenches to assign NPCs to workbenches.
+ The system SHALL create a persistent association between NPC and assigned location.

#### Scenario: Player assigns NPC to bed
+ GIVEN a player has an NPC item in a quickslot
+ AND a bed exists nearby
+ WHEN the player uses the NPC item on the bed
+ THEN the NPC is assigned to the bed
+ AND the NPC becomes available at that location
+ AND the assignment is persistent (survives game sessions)

#### Scenario: Player assigns NPC to workbench
+ GIVEN a player has an NPC item in a quickslot
+ AND a workbench exists nearby
+ WHEN the player uses the NPC item on the workbench
+ THEN the NPC is assigned to the workbench
+ AND the NPC becomes available at that location
+ AND the assignment is persistent (survives game sessions)

#### Scenario: NPC assignment creates location association
+ GIVEN an NPC has been assigned to a bed or workbench
+ WHEN the system tracks NPC state
+ THEN the NPC is associated with the assigned location
+ AND the association persists until NPC is reassigned or removed
+ AND the NPC can be interacted with at the assigned location

### Requirement: NPC assignment state tracking
+ The system SHALL track which NPCs are assigned to which locations.
+ The system SHALL allow NPCs to be reassigned to different locations.
+ The system SHALL preserve NPC type and properties during assignment.

#### Scenario: System tracks NPC assignments
+ GIVEN multiple NPCs have been assigned to different locations
+ WHEN the system queries NPC assignments
+ THEN the system returns all NPC-location associations
+ AND each NPC is associated with exactly one location (or unassigned)

#### Scenario: Player reassigns NPC
+ GIVEN an NPC is assigned to a bed
+ WHEN the player uses the NPC item on a different bed or workbench
+ THEN the NPC is reassigned to the new location
+ AND the previous assignment is removed
+ AND the NPC becomes available at the new location

#### Scenario: NPC retains type and properties
+ GIVEN an NPC of a specific type (e.g., Blacksmith) is assigned to a location
+ WHEN the NPC is assigned
+ THEN the NPC retains its type and specialization
+ AND the NPC retains all properties (level, stats, etc.)
+ AND the NPC type determines available interactions and capabilities
