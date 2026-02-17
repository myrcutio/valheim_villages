# npc-interaction-menu - Spec Delta

## ADDED Requirements

### Requirement: NPC interaction menu display
+ The system SHALL display a workbench-style menu when players interact with assigned NPCs.
+ The system SHALL show context-dependent options based on NPC type and unlocked features.
+ The system SHALL provide multiple tabs for different interaction types.

#### Scenario: Player interacts with NPC
+ GIVEN an NPC is assigned to a location
+ AND the player is near the NPC
+ WHEN the player interacts with the NPC
+ THEN a workbench-style menu is displayed
+ AND the menu shows available interaction options
+ AND the menu has multiple tabs (options, crafting, upgrade)

#### Scenario: Menu shows context-dependent options
+ GIVEN a player interacts with an NPC
+ AND the NPC has certain features unlocked
+ WHEN the menu is displayed
+ THEN available options are shown based on NPC type
+ AND available options are shown based on unlocked features
+ AND unavailable options are hidden or grayed out

### Requirement: NPC interaction options
+ The system SHALL provide initial interaction options such as "Hop on!" and "Have a drink with me!".
+ The system SHALL unlock additional options as NPC mood and conditions improve.
+ The system SHALL display options appropriate to the NPC type.

#### Scenario: Initial interaction options available
+ GIVEN a player interacts with an NPC
+ WHEN the menu is displayed
+ THEN initial options are available: "Hop on!" (mount/ride) and "Have a drink with me!" (social interaction)
+ AND these options are available regardless of NPC mood or conditions

#### Scenario: Additional options unlock with improved conditions
+ GIVEN a player interacts with an NPC
+ AND the NPC's mood has improved
+ AND certain conditions have been met
+ WHEN the menu is displayed
+ THEN additional interaction options become available
+ AND the new options are appropriate to the NPC type and improved conditions

### Requirement: NPC crafting menu
+ The system SHALL provide a crafting tab in the NPC interaction menu.
+ The system SHALL allow players to produce items through NPC interaction.
+ The system SHALL produce NPC-specific items such as "swaddled villager" and "map fragment".

#### Scenario: Player accesses NPC crafting menu
+ GIVEN a player interacts with an NPC
+ WHEN the player opens the crafting tab
+ THEN a crafting menu is displayed
+ AND the menu shows items that can be produced through NPC interaction
+ AND the menu follows workbench-style crafting interface

#### Scenario: Player produces swaddled villager
+ GIVEN a player interacts with an NPC
+ AND the player opens the crafting menu
+ WHEN the player crafts a "swaddled villager"
+ THEN the item is produced
+ AND the item represents the NPC in packaged form
+ AND the item can be placed in inventory or quickslot

#### Scenario: Player produces map fragment
+ GIVEN a player interacts with an NPC (e.g., Scout)
+ AND the player opens the crafting menu
+ WHEN the player crafts a "map fragment"
+ THEN the item is produced
+ AND the item is related to exploration or map discovery
+ AND the item can be used for NPC location discovery (as defined in npc-location-discovery)

#### Scenario: NPC type determines available items
+ GIVEN a player interacts with different NPC types
+ WHEN the player opens the crafting menu for each NPC type
+ THEN different NPC types show different available items
+ AND items are appropriate to the NPC's type and specialization
+ AND items reflect the NPC's capabilities

### Requirement: NPC upgrade tab
+ The system SHALL provide an upgrade tab in the NPC interaction menu.
+ The system SHALL display current NPC mood level and requirements.
+ The system SHALL allow players to assign nearby locations to improve NPC mood.
+ The system SHALL allow players to bribe NPCs with mead or gold for temporary mood boosts.

#### Scenario: Player accesses upgrade tab
+ GIVEN a player interacts with an NPC
+ WHEN the player opens the upgrade tab
+ THEN the upgrade interface is displayed
+ AND current NPC mood level is shown
+ AND mood requirements for work are displayed
+ AND options for improving mood are available

#### Scenario: Player assigns nearby location
+ GIVEN a player interacts with an NPC
+ AND the player opens the upgrade tab
+ AND a bed or feast exists nearby
+ WHEN the player assigns the nearby location to the NPC
+ THEN the location is associated with the NPC
+ AND the NPC's mood improves permanently
+ AND the mood improvement is reflected in the upgrade tab

#### Scenario: Player bribes NPC with mead
+ GIVEN a player interacts with an NPC
+ AND the player opens the upgrade tab
+ AND the player has mead in inventory
+ WHEN the player provides mead as a bribe
+ THEN the mead is consumed
+ AND the NPC receives a temporary mood boost
+ AND the mood boost is displayed in the upgrade tab
+ AND the mood boost will decay over time

#### Scenario: Player bribes NPC with gold
+ GIVEN a player interacts with an NPC
+ AND the player opens the upgrade tab
+ AND the player has gold in inventory
+ WHEN the player provides gold as a bribe
+ THEN the gold is consumed
+ AND the NPC receives a temporary mood boost
+ AND the mood boost is displayed in the upgrade tab
+ AND the mood boost will decay over time
