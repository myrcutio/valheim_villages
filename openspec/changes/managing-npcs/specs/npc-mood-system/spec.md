# npc-mood-system - Spec Delta

## ADDED Requirements

### Requirement: NPC mood level tracking
+ The system SHALL track a numeric mood level for each NPC (integer levels, similar to comfort system).
+ The system SHALL calculate mood level from contributing factors (bed, fire, mead, food, location assignments, etc.), similar to how comfort works.
+ The system SHALL update mood level based on contributing factors.
+ The system SHALL persist mood level across game sessions.

#### Scenario: System tracks NPC mood
+ GIVEN an NPC is assigned to a location
+ WHEN the system tracks NPC state
+ THEN the NPC has a numeric mood level (integer, similar to comfort levels)
+ AND the mood level is calculated from contributing factors (bed, fire, mead, food, etc.)
+ AND the mood level is stored and persisted
+ AND the mood level can be queried and displayed

#### Scenario: Mood level persists across sessions
+ GIVEN an NPC has a mood level of 75
+ WHEN the game session ends and restarts
+ THEN the NPC's mood level remains 75
+ AND mood level is preserved across game sessions

### Requirement: Mood level sources
+ The system SHALL calculate mood level from contributing factors similar to comfort system (bed, fire, mead, food, etc.).
+ The system SHALL calculate mood level from location assignments (permanent improvements).
+ The system SHALL calculate mood level from temporary bribes (mead/gold with time-based decay).
+ The system SHALL combine all mood sources to determine total mood level.

#### Scenario: Mood calculated from contributing factors
+ GIVEN an NPC is assigned to a location
+ AND the location has a bed nearby
+ AND the location has a fire nearby
+ AND the NPC has access to food
+ AND the NPC has access to mead
+ WHEN the system calculates mood level
+ THEN the mood level is calculated from all contributing factors (bed, fire, food, mead, etc.)
+ AND the calculation works similarly to Valheim's comfort system
+ AND each contributing factor adds to the total mood level

#### Scenario: Location assignment improves mood
+ GIVEN an NPC has a base mood level
+ AND the player assigns a nearby bed to the NPC
+ WHEN the system calculates mood level
+ THEN the mood level increases by a fixed amount (permanent improvement)
+ AND the improvement persists until the location assignment is removed

#### Scenario: Temporary bribe improves mood
+ GIVEN an NPC has a current mood level
+ AND the player provides mead or gold as a bribe
+ WHEN the system calculates mood level
+ THEN the mood level increases temporarily
+ AND the temporary boost is tracked separately from permanent improvements
+ AND the temporary boost will decay over time

#### Scenario: Permanent and temporary mood combined
+ GIVEN an NPC has permanent mood from location assignments
+ AND the NPC has temporary mood from bribes
+ WHEN the system calculates total mood level
+ THEN permanent and temporary mood are combined
+ AND the total mood level is the sum of both sources
+ AND temporary mood decay does not affect permanent mood

### Requirement: Mood decay system
+ The system SHALL decay temporary mood benefits from mead or gold over time.
+ The system SHALL track decay timers for each temporary mood source.
+ The system SHALL decay mead mood boosts after one day/night cycle.
+ The system SHALL decay gold mood boosts after 3 days.

#### Scenario: Mead mood boost decays after one day/night cycle
+ GIVEN an NPC has received a temporary mood boost from mead
+ WHEN one day/night cycle passes in the game
+ THEN the mead mood boost expires
+ AND the mood boost is removed
+ AND the NPC's mood returns to its base level (without the mead boost)

#### Scenario: Gold mood boost decays after 3 days
+ GIVEN an NPC has received a temporary mood boost from gold
+ WHEN 3 days pass in the game
+ THEN the gold mood boost expires
+ AND the mood boost is removed
+ AND the NPC's mood returns to its base level (without the gold boost)
+ AND the NPC can no longer work regardless of mood (unless mood level is 3+)

#### Scenario: Multiple temporary boosts decay independently
+ GIVEN an NPC has received multiple temporary mood boosts (from different bribes)
+ WHEN time passes in the game
+ THEN each temporary boost decays independently
+ AND each boost has its own decay timer
+ AND mead boosts expire after one day/night cycle
+ AND gold boosts expire after 3 days

### Requirement: Mood level behaviors
+ The system SHALL implement different behaviors based on NPC mood level.
+ The system SHALL make NPCs wander around crying when mood level is 0.
+ The system SHALL make NPCs wander around the village when mood level is 1.
+ The system SHALL enable NPC conversations with the player when mood level is 2.
+ The system SHALL enable NPCs to seek quotas when mood level is 3 or higher.

#### Scenario: NPC at mood level 0
+ GIVEN an NPC has a mood level of 0
+ WHEN the NPC's behavior is determined
+ THEN the NPC wanders around crying
+ AND the NPC does not have conversations
+ AND the NPC does not seek quotas

#### Scenario: NPC at mood level 1
+ GIVEN an NPC has a mood level of 1
+ WHEN the NPC's behavior is determined
+ THEN the NPC wanders around the village
+ AND the NPC does not have conversations
+ AND the NPC does not seek quotas

#### Scenario: NPC at mood level 2
+ GIVEN an NPC has a mood level of 2
+ WHEN the NPC's behavior is determined
+ THEN the NPC wanders around the village
+ AND the NPC can have conversations with the player
+ AND the NPC does not seek quotas

#### Scenario: NPC at mood level 3 or higher
+ GIVEN an NPC has a mood level of 3 or higher
+ WHEN the NPC's behavior is determined
+ THEN the NPC wanders around the village
+ AND the NPC can have conversations with the player
+ AND the NPC can seek and work on quotas (if mood is sufficient for specific quota)

### Requirement: Mood threshold for quota work
+ The system SHALL require NPCs to have mood level 3 or higher before working on quotas.
+ The system SHALL allow NPCs with active gold bribes to work on quotas regardless of mood level (for 3 days).
+ The system SHALL check mood level or gold bribe status when NPCs attempt to seek quotas.
+ The system SHALL prevent quota work if mood level is below 3 and no gold bribe is active.

#### Scenario: NPC at mood level 3+ can seek quotas
+ GIVEN an NPC has a mood level of 3 or higher
+ WHEN the NPC attempts to seek quotas
+ THEN the mood level check passes
+ AND the NPC can seek and work on quotas

#### Scenario: NPC with gold bribe can seek quotas regardless of mood
+ GIVEN an NPC has an active gold bribe (within 3 days)
+ AND the NPC's mood level is below 3
+ WHEN the NPC attempts to seek quotas
+ THEN the gold bribe check passes
+ AND the NPC can seek and work on quotas regardless of mood level

#### Scenario: NPC below mood level 3 cannot work without gold bribe
+ GIVEN an NPC has a mood level below 3
+ AND the NPC does not have an active gold bribe
+ WHEN the NPC attempts to seek quotas
+ THEN the mood level check fails
+ AND the NPC cannot seek quotas
+ AND the NPC remains idle or wanders

#### Scenario: Mood level prevents work even if quota available
+ GIVEN an NPC has a mood level below 3
+ AND the NPC does not have an active gold bribe
+ AND a quota is available nearby
+ WHEN the NPC would normally seek the quota
+ THEN the NPC does not seek the quota
+ AND the NPC remains idle or wanders until mood level reaches 3 or higher (or receives gold bribe)
