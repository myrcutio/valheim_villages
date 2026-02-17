# npc-upgrade-system - Spec Delta

## ADDED Requirements

### Requirement: Location assignment system
+ The system SHALL allow players to assign nearby locations (bed, feast, etc.) to NPCs.
+ The system SHALL provide permanent mood improvements when locations are assigned.
+ The system SHALL track which locations are assigned to which NPCs.

#### Scenario: Player assigns bed to NPC
+ GIVEN a player interacts with an NPC
+ AND a bed exists nearby
+ WHEN the player assigns the bed to the NPC through the upgrade tab
+ THEN the bed is associated with the NPC
+ AND the NPC receives a permanent mood improvement
+ AND the mood improvement persists until the assignment is removed

#### Scenario: Player assigns feast to NPC
+ GIVEN a player interacts with an NPC
+ AND a feast exists nearby
+ WHEN the player assigns the feast to the NPC through the upgrade tab
+ THEN the feast is associated with the NPC
+ AND the NPC receives a permanent mood improvement
+ AND the NPC gains comfort benefits from the feast
+ AND the mood improvement persists until the assignment is removed

#### Scenario: System tracks location assignments
+ GIVEN multiple NPCs have assigned locations
+ WHEN the system tracks assignments
+ THEN each NPC's assigned locations are tracked
+ AND assignments can be queried and displayed
+ AND assignments persist across game sessions

### Requirement: Bribe system
+ The system SHALL allow players to bribe NPCs with mead for temporary mood boosts.
+ The system SHALL allow players to bribe NPCs with gold for temporary mood boosts.
+ The system SHALL consume mead or gold when bribes are provided.
+ The system SHALL provide different boost levels based on mead type or gold amount.

#### Scenario: Player bribes NPC with mead
+ GIVEN a player interacts with an NPC
+ AND the player has mead in inventory
+ WHEN the player provides mead as a bribe through the upgrade tab
+ THEN the mead is consumed from inventory
+ AND the NPC receives a temporary mood boost
+ AND the boost level depends on mead type (basic mead vs. advanced mead)

#### Scenario: Player bribes NPC with gold
+ GIVEN a player interacts with an NPC
+ AND the player has gold in inventory
+ WHEN the player provides gold as a bribe through the upgrade tab
+ THEN the gold is consumed from inventory
+ AND the NPC receives a temporary mood boost
+ AND the boost level depends on gold amount (more gold = larger boost)
+ AND the gold bribe allows the NPC to work on quotas for 3 days regardless of mood level

#### Scenario: Different mead types provide different boosts
+ GIVEN a player has different types of mead (basic, medium, advanced)
+ WHEN the player provides each type as a bribe
+ THEN basic mead provides a small mood boost
+ AND advanced mead provides a larger mood boost
+ AND boost levels scale with mead quality

### Requirement: Temporary mood boost tracking
+ The system SHALL track temporary mood boosts from bribes separately from permanent mood.
+ The system SHALL track decay timers for each temporary boost.
+ The system SHALL decay temporary boosts gradually over time.

#### Scenario: Temporary boosts tracked separately
+ GIVEN an NPC has permanent mood from location assignments
+ AND the NPC receives a temporary boost from a bribe
+ WHEN the system tracks mood
+ THEN permanent and temporary mood are tracked separately
+ AND temporary boosts have associated decay timers
+ AND total mood is the sum of permanent and temporary sources

#### Scenario: Temporary boost decays over time
+ GIVEN an NPC has received a temporary mood boost from mead
+ WHEN one day/night cycle passes in the game
+ THEN the mead boost expires and is removed
+ AND the decay is tracked by the decay timer

#### Scenario: Gold boost expires after 3 days
+ GIVEN an NPC has received a temporary mood boost from gold
+ WHEN 3 days pass in the game
+ THEN the gold boost expires and is removed
+ AND the decay is tracked by the decay timer
+ AND the NPC can no longer work regardless of mood (unless mood level is 3+)

#### Scenario: Multiple temporary boosts decay independently
+ GIVEN an NPC has received multiple temporary boosts from different bribes (mead and gold)
+ WHEN time passes in the game
+ THEN each boost decays independently
+ AND each boost has its own decay timer
+ AND mead boosts expire after one day/night cycle
+ AND gold boosts expire after 3 days

### Requirement: Upgrade tab display
+ The system SHALL display current mood level in the upgrade tab.
+ The system SHALL display mood requirements for work in the upgrade tab.
+ The system SHALL show available location assignments in the upgrade tab.
+ The system SHALL show bribe options (mead/gold) in the upgrade tab.

#### Scenario: Upgrade tab shows current mood
+ GIVEN a player interacts with an NPC
+ AND the player opens the upgrade tab
+ WHEN the upgrade tab is displayed
+ THEN current NPC mood level is shown
+ AND mood requirements for work are displayed
+ AND the player can see if mood threshold is met

#### Scenario: Upgrade tab shows location assignments
+ GIVEN a player interacts with an NPC
+ AND nearby locations (bed, feast) exist
+ WHEN the player opens the upgrade tab
+ THEN available location assignments are shown
+ AND already assigned locations are indicated
+ AND the player can assign or unassign locations

#### Scenario: Upgrade tab shows bribe options
+ GIVEN a player interacts with an NPC
+ AND the player has mead or gold in inventory
+ WHEN the player opens the upgrade tab
+ THEN bribe options are shown (mead, gold)
+ AND available mead types or gold amounts are displayed
+ AND the player can provide bribes to boost mood temporarily
