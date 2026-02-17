# job-board-system - Spec Delta

## ADDED Requirements

### Requirement: Job Board discovery
+ The system SHALL discover/unlock the Job Board building when a player picks up an NPC for the first time.
+ The system SHALL make the Job Board available for construction after discovery.
+ The system SHALL track Job Board discovery state.

#### Scenario: Job Board discovered on first NPC pickup
+ GIVEN a player picks up an NPC for the first time (acquires NPC item)
+ WHEN the NPC is acquired
+ THEN the Job Board building is discovered/unlocked
+ AND the Job Board becomes available in the building menu
+ AND the discovery state is persisted

#### Scenario: Job Board available after discovery
+ GIVEN a player has discovered the Job Board
+ WHEN the player accesses the building menu
+ THEN the Job Board is available for construction
+ AND the player can build Job Boards at their base or village

### Requirement: Job Board building
+ The system SHALL provide a Job Board building type (storage chest-like structure).
+ The system SHALL allow players to build Job Boards at their base or village.
+ The system SHALL store quotas in Job Boards.

#### Scenario: Player builds Job Board
+ GIVEN a player has discovered the Job Board
+ AND the player has required materials
+ WHEN the player constructs a Job Board
+ THEN the Job Board is placed in the world
+ AND the Job Board functions as a storage container for quotas

#### Scenario: Job Board stores quotas
+ GIVEN a Job Board exists
+ WHEN the player interacts with the Job Board
+ THEN the Job Board opens like a storage chest
+ AND quotas (scroll items) can be placed in or removed from the Job Board
+ AND the Job Board displays stored quotas

### Requirement: Quota item system
+ The system SHALL provide quota items as scrolls describing tasks.
+ The system SHALL allow quotas to describe various tasks NPCs can perform.
+ The system SHALL make quotas placeable in Job Boards.
+ The system SHALL define quotas with two parameters: an output product and a desired quantity.

#### Scenario: Quota describes a task
+ GIVEN a quota item exists
+ WHEN the player examines the quota
+ THEN the quota displays a task description (e.g., "Cook food", "Chop lumber", "Patrol area")
+ AND the quota indicates what the NPC should do
+ AND the quota may indicate requirements or conditions

#### Scenario: Quota contains output product and desired quantity
+ GIVEN a quota item exists
+ WHEN the player examines the quota
+ THEN the quota specifies an output product (e.g., roast boar meat, wood, copper ore)
+ AND the quota specifies a desired quantity
+ AND the quota indicates the task is to produce the output product in the desired quantity

#### Scenario: Quota can be placed in Job Board
+ GIVEN a player has a quota item
+ AND a Job Board exists
+ WHEN the player interacts with the Job Board
+ THEN the quota can be placed in the Job Board
+ AND the quota is stored in the Job Board
+ AND the quota becomes available for NPCs to work on

### Requirement: Quota task types
+ The system SHALL support quotas for cooking food tasks.
+ The system SHALL support quotas for chopping lumber tasks.
+ The system SHALL support quotas for patrolling area tasks.
+ The system SHALL support quotas for other daily tasks or chores that players can perform.
+ The system SHALL be generalized enough to support most player-available tasks.

#### Scenario: Quota for cooking food
+ GIVEN a quota describes a cooking food task
+ WHEN an NPC works on the quota
+ THEN the NPC performs cooking-related actions
+ AND the quota system supports cooking task execution

#### Scenario: Quota for chopping lumber
+ GIVEN a quota describes a chopping lumber task
+ WHEN an NPC works on the quota
+ THEN the NPC performs woodcutting actions
+ AND the quota system supports lumber chopping task execution

#### Scenario: Quota for patrolling area
+ GIVEN a quota describes a patrolling area task
+ WHEN an NPC works on the quota
+ THEN the NPC performs patrol/movement actions
+ AND the quota system supports patrolling task execution

#### Scenario: Quota system supports various tasks
+ GIVEN quotas exist for different task types
+ WHEN the system processes quotas
+ THEN the system can handle cooking, chopping, patrolling, mining, crafting, and other tasks
+ AND the quota system is generalized for expandability
+ AND new task types can be added without major system changes

### Requirement: Quota storage and management
+ The system SHALL allow multiple quotas to be stored in a Job Board.
+ The system SHALL allow players to add or remove quotas from Job Boards.
+ The system SHALL track which quotas are available for NPCs to work on.

#### Scenario: Multiple quotas in Job Board
+ GIVEN a Job Board exists
+ AND multiple quota items exist
+ WHEN the player places multiple quotas in the Job Board
+ THEN all quotas are stored in the Job Board
+ AND the Job Board can hold multiple quotas simultaneously
+ AND NPCs can see all available quotas

#### Scenario: Player manages quotas in Job Board
+ GIVEN a Job Board contains quotas
+ WHEN the player interacts with the Job Board
+ THEN the player can add new quotas
+ AND the player can remove existing quotas
+ AND quota management follows standard storage chest interface
