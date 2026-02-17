# npc-basic-needs - Spec Delta

## ADDED Requirements

### Requirement: NPC basic needs tracking
+ The system SHALL track four basic needs for each NPC: bed, food, fire, and safety.
+ The system SHALL periodically check if NPC basic needs are met.
+ The system SHALL apply a "stressed" debuff when any basic need is unmet.

#### Scenario: System tracks NPC basic needs
+ GIVEN an NPC is assigned to a location
+ WHEN the system checks NPC basic needs
+ THEN the system evaluates four needs: bed access, food access, fire access, and safety from enemies
+ AND the system tracks which needs are met and which are unmet

#### Scenario: NPC has all basic needs met
+ GIVEN an NPC is assigned to a location
+ AND the location has a nearby bed
+ AND the location has access to food
+ AND the location has a fire source (campfire, hearth, etc.)
+ AND the location is in a safe room (enclosed structure) protected from roaming enemies
+ WHEN the system checks basic needs
+ THEN all four needs are marked as met
+ AND the NPC does not receive the stressed debuff

#### Scenario: NPC missing basic need receives stressed debuff
+ GIVEN an NPC is assigned to a location
+ AND at least one basic need is unmet (bed, food, fire, or safety)
+ WHEN the system checks basic needs
+ THEN the unmet need is identified
+ AND the NPC receives the "stressed" debuff
+ AND the stressed debuff prevents the NPC from working
+ AND the stressed debuff prevents the NPC from providing benefits

### Requirement: Bed need validation
+ The system SHALL check if NPC has access to a bed.
+ The system SHALL validate that the bed is within reasonable proximity to the NPC's assigned location.

#### Scenario: NPC has bed access
+ GIVEN an NPC is assigned to a location
+ AND a bed exists within reasonable proximity
+ WHEN the system checks bed need
+ THEN bed need is marked as met
+ AND the NPC can use the bed for sleeping

#### Scenario: NPC lacks bed access
+ GIVEN an NPC is assigned to a location
+ AND no bed exists within reasonable proximity
+ WHEN the system checks bed need
+ THEN bed need is marked as unmet
+ AND the NPC receives stressed debuff (if other needs are also unmet)

### Requirement: Food need validation
+ The system SHALL check if NPC has access to food.
+ The system SHALL validate that food is available within reasonable proximity or in storage accessible to the NPC.

#### Scenario: NPC has food access
+ GIVEN an NPC is assigned to a location
+ AND food is available (in nearby storage, food preparation area, or accessible containers)
+ WHEN the system checks food need
+ THEN food need is marked as met
+ AND the NPC can access food for consumption

#### Scenario: NPC lacks food access
+ GIVEN an NPC is assigned to a location
+ AND no food is available within reasonable proximity or accessible storage
+ WHEN the system checks food need
+ THEN food need is marked as unmet
+ AND the NPC receives stressed debuff (if other needs are also unmet)

### Requirement: Fire need validation
+ The system SHALL check if NPC has access to a fire source.
+ The system SHALL validate that a fire source (campfire, hearth, etc.) exists within reasonable proximity.

#### Scenario: NPC has fire access
+ GIVEN an NPC is assigned to a location
+ AND a fire source (campfire, hearth, etc.) exists within reasonable proximity
+ WHEN the system checks fire need
+ THEN fire need is marked as met
+ AND the NPC can benefit from the fire (warmth, light, cooking)

#### Scenario: NPC lacks fire access
+ GIVEN an NPC is assigned to a location
+ AND no fire source exists within reasonable proximity
+ WHEN the system checks fire need
+ THEN fire need is marked as unmet
+ AND the NPC receives stressed debuff (if other needs are also unmet)

### Requirement: Safety need validation
+ The system SHALL check if NPC is in a safe room protected from roaming enemies.
+ The system SHALL validate that the NPC's location is in an enclosed structure that prevents enemy access.

#### Scenario: NPC is in safe room
+ GIVEN an NPC is assigned to a location
+ AND the location is in an enclosed structure (walls, roof, doors) that prevents roaming enemy access
+ WHEN the system checks safety need
+ THEN safety need is marked as met
+ AND the NPC is protected from roaming enemies

#### Scenario: NPC is not in safe room
+ GIVEN an NPC is assigned to a location
+ AND the location is not in an enclosed structure or is exposed to roaming enemies
+ WHEN the system checks safety need
+ THEN safety need is marked as unmet
+ AND the NPC receives stressed debuff (if other needs are also unmet)

### Requirement: Stressed debuff effects
+ The system SHALL apply stressed debuff when any basic need is unmet.
+ The system SHALL prevent NPCs with stressed debuff from working on quotas.
+ The system SHALL prevent NPCs with stressed debuff from providing benefits to player or village.

#### Scenario: Stressed NPC cannot work
+ GIVEN an NPC has the stressed debuff (at least one basic need unmet)
+ WHEN the NPC attempts to work on a quota
+ THEN the NPC cannot work
+ AND the quota work is prevented
+ AND the NPC remains idle

#### Scenario: Stressed NPC provides no benefits
+ GIVEN an NPC has the stressed debuff (at least one basic need unmet)
+ WHEN the system checks NPC benefits
+ THEN the NPC does not provide any benefits to the player
+ AND the NPC does not provide any benefits to the village
+ AND NPC type-specific benefits are disabled

#### Scenario: Stressed debuff removed when needs met
+ GIVEN an NPC has the stressed debuff
+ AND all basic needs become met (bed, food, fire, safety)
+ WHEN the system checks basic needs
+ THEN the stressed debuff is removed
+ AND the NPC can work on quotas
+ AND the NPC can provide benefits again
