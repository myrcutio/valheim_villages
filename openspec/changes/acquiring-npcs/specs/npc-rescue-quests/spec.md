# npc-rescue-quests - Spec Delta

## ADDED Requirements

### Requirement: Captive NPC placement
+ The system SHALL place captive NPCs at various locations throughout the world.
+ The system SHALL associate captive NPCs with captor entities (enemies, hostile creatures).
+ The system SHALL track the state of each captive NPC (captive, rescued, assigned).

#### Scenario: NPC placed in world as captive
+ GIVEN the world is generated or a new area is explored
+ WHEN a captive NPC location is created
+ THEN a captive NPC is placed at the location
+ AND captor entities (enemies) are spawned to guard the NPC
+ AND the NPC's state is set to "captive"

#### Scenario: Captive NPC has appropriate captors
+ GIVEN a captive NPC is placed in the world
+ WHEN the system spawns captor entities
+ THEN captors are appropriate for the location and biome
+ AND captor difficulty matches the NPC type and location
+ AND captors are positioned to guard the captive NPC

### Requirement: Rescue quest initiation
+ The system SHALL allow players to initiate rescue quests when they discover captive NPC locations.
+ The system SHALL track rescue quest state (discovered, in progress, completed).
+ The system SHALL provide clear indication when a rescue quest is available.

#### Scenario: Player discovers captive NPC location
+ GIVEN a player has discovered the location of a captive NPC
+ WHEN the player approaches the location
+ THEN the rescue quest becomes available
+ AND the player can see the captive NPC and captors
+ AND the quest state is set to "discovered"

#### Scenario: Player initiates rescue quest
+ GIVEN a rescue quest is available (NPC location discovered)
+ WHEN the player engages with the rescue encounter
+ THEN the rescue quest state changes to "in progress"
+ AND the player must defeat captors or complete challenges to rescue the NPC

### Requirement: Rescue encounter mechanics
+ The system SHALL create rescue encounters with captor entities that must be defeated.
+ The system SHALL allow rescue attempts to succeed or fail.
+ The system SHALL handle rescue failure gracefully (NPC remains captive, can retry).

#### Scenario: Player successfully rescues NPC
+ GIVEN a rescue quest is in progress
+ AND the player engages with captor entities
+ WHEN the player defeats all captors
+ THEN the captive NPC is freed
+ AND the rescue quest state changes to "completed"
+ AND the NPC becomes available for assignment to the player's village

#### Scenario: Player fails rescue attempt
+ GIVEN a rescue quest is in progress
+ AND the player engages with captor entities
+ WHEN the player is defeated or retreats
+ THEN the rescue quest state remains "in progress" or resets to "discovered"
+ AND the NPC remains captive
+ AND the player can attempt the rescue again

#### Scenario: Rescue encounter difficulty
+ GIVEN a captive NPC location exists
+ WHEN a rescue encounter is initiated
+ THEN captor difficulty is appropriate for the NPC type and location
+ AND encounter difficulty may scale with player progression
+ AND the encounter provides a meaningful challenge

### Requirement: Post-rescue NPC assignment
+ The system SHALL make rescued NPCs available for assignment to the player's village.
+ The system SHALL integrate rescued NPCs with existing villager assignment systems.
+ The system SHALL track which NPCs have been rescued and assigned.

#### Scenario: Rescued NPC becomes available
+ GIVEN a player has successfully rescued an NPC
+ WHEN the rescue quest is completed
+ THEN the NPC becomes available for assignment
+ AND the NPC can be assigned to the player's village or constructions
+ AND the NPC's type and specialization are preserved

#### Scenario: Rescued NPC assignment integration
+ GIVEN a rescued NPC is available for assignment
+ WHEN the player attempts to assign the NPC to a village or construction
+ THEN the system uses existing villager assignment logic
+ AND residency requirements are checked (if applicable)
+ AND the NPC is assigned following standard assignment procedures

### Requirement: NPC types and specializations
+ The system SHALL support all NPC types defined in villager-npc-types (Farmer, Miner, Blacksmith, Carpenter, Scout, Trader, Guard, Mountaineer, Shipwright, Tavern Keeper).
+ The system SHALL preserve NPC type and specialization after rescue.
+ The system SHALL enable NPC types to provide different benefits or capabilities based on their category (Villager vs. Specialist).

#### Scenario: Rescued NPC retains type
+ GIVEN a blacksmith NPC is rescued
+ WHEN the NPC is assigned to a village
+ THEN the NPC retains blacksmith type and specialization
+ AND the NPC provides blacksmith-related production or capabilities (as defined in villager-npc-types)
+ AND the NPC type is displayed to the player

#### Scenario: Different NPC types provide different benefits
+ GIVEN multiple NPC types are rescued (farmer, miner, blacksmith, scout, guard)
+ WHEN NPCs are assigned to villages
+ THEN each NPC type provides benefits appropriate to their specialization
+ AND villager NPCs (farmer, miner, blacksmith) provide production output
+ AND specialist NPCs (scout, guard) provide direct gameplay benefits
+ AND benefits match the definitions in villager-npc-types proposal
