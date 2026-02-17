# npc-quota-work - Spec Delta

## ADDED Requirements

### Requirement: NPC quota seeking behavior
+ The system SHALL allow NPCs to seek quotas from nearby Job Boards when mood level is 3 or higher.
+ The system SHALL allow NPCs with active gold bribes to seek quotas regardless of mood level (for 3 days).
+ The system SHALL check NPC mood level or gold bribe status before allowing quota seeking.
+ The system SHALL prioritize Job Boards nearest to the NPC's assigned bed.

#### Scenario: NPC seeks quota when mood level 3+
+ GIVEN an NPC has a mood level of 3 or higher
+ AND the NPC is assigned to a bed
+ AND a Job Board with quotas exists nearby
+ WHEN the NPC attempts to seek quotas
+ THEN the mood level check passes
+ AND the NPC searches for quotas in Job Boards nearest to their bed first
+ AND the NPC can find and select an appropriate quota

#### Scenario: NPC seeks quota with active gold bribe regardless of mood
+ GIVEN an NPC has an active gold bribe (within 3 days)
+ AND the NPC's mood level is below 3
+ AND the NPC is assigned to a bed
+ AND a Job Board with quotas exists nearby
+ WHEN the NPC attempts to seek quotas
+ THEN the gold bribe check passes
+ AND the NPC can seek quotas regardless of mood level
+ AND the NPC searches for quotas in Job Boards nearest to their bed first
+ AND the NPC can find and select an appropriate quota

#### Scenario: NPC does not seek quota when mood below 3 and no gold bribe
+ GIVEN an NPC has a mood level below 3
+ AND the NPC does not have an active gold bribe
+ AND a Job Board with quotas exists nearby
+ WHEN the NPC would normally seek quotas
+ THEN the mood level check fails
+ AND the NPC does not seek quotas
+ AND the NPC remains idle or wanders (based on mood level)

#### Scenario: NPC prioritizes Job Boards nearest to bed
+ GIVEN an NPC is assigned to a bed
+ AND multiple Job Boards exist at different distances from the bed
+ WHEN the NPC seeks quotas
+ THEN the NPC searches Job Boards nearest to their bed first
+ AND the NPC prioritizes closer Job Boards over distant ones

### Requirement: Quota selection logic
+ The system SHALL allow NPCs to select quotas from Job Boards starting from the top left slot, scanning right and down.
+ The system SHALL allow NPCs to select appropriate quotas based on NPC type and capabilities.
+ The system SHALL handle quota selection when multiple quotas are available.
+ The system SHALL allow NPCs to take the quota item while working on it.

#### Scenario: NPC selects quota from top left, scanning right and down
+ GIVEN an NPC has mood level 3 or higher (or has active gold bribe)
+ AND a Job Board contains multiple quotas in different slots
+ WHEN the NPC selects a quota
+ THEN the NPC starts from the top left slot
+ AND the NPC scans right and down through available slots
+ AND the NPC selects the first available quota that matches their capabilities

#### Scenario: NPC takes quota item while working
+ GIVEN an NPC has selected a quota from a Job Board
+ WHEN the NPC begins working on the quota
+ THEN the NPC takes the quota item from the Job Board
+ AND the quota item is held by the NPC during work
+ AND the quota slot in the Job Board is marked as in use

#### Scenario: NPC selects quota matching type
+ GIVEN an NPC of a specific type (e.g., Farmer)
+ AND multiple quotas are available in a Job Board
+ WHEN the NPC scans quotas (top left, right and down)
+ THEN the NPC prioritizes quotas that match its type and capabilities
+ AND a Farmer NPC may select cooking or farming-related quotas
+ AND a Miner NPC may select mining-related quotas
+ AND the NPC selects the first matching quota found during the scan

#### Scenario: NPC selects from multiple available quotas
+ GIVEN an NPC has mood level 3 or higher (or has active gold bribe)
+ AND multiple appropriate quotas are available in a Job Board
+ WHEN the NPC scans the Job Board (top left, right and down)
+ THEN the NPC selects the first available quota that matches their capabilities
+ AND the selected quota becomes the NPC's active work task
+ AND the NPC takes the quota item

### Requirement: Quota work execution
+ The system SHALL allow NPCs to execute the tasks described in quotas.
+ The system SHALL perform work actions appropriate to the quota task type.
+ The system SHALL track work progress for each quota (current quantity vs. desired quantity).
+ The system SHALL store output products in the same chest (Job Board) where the quota was found, if possible.

#### Scenario: NPC executes quota and stores output product
+ GIVEN an NPC has selected a quota with an output product (e.g., roast boar meat) and desired quantity
+ AND the NPC has access to required resources and equipment
+ AND the Job Board (chest) has space available
+ WHEN the NPC works on the quota and produces output products
+ THEN the NPC performs work actions appropriate to the quota type
+ AND the NPC produces the output product
+ AND the output product is stored in the same Job Board where the quota was found
+ AND work progress is tracked (current quantity produced vs. desired quantity)

#### Scenario: NPC executes cooking quota
+ GIVEN an NPC has selected a cooking quota with output product (e.g., roast boar meat) and desired quantity
+ AND the NPC has access to required resources and equipment
+ AND the Job Board has space available
+ WHEN the NPC works on the quota
+ THEN the NPC performs cooking actions
+ AND the NPC uses food preparation equipment
+ AND the NPC produces the output product (cooked food items)
+ AND the output product is stored in the Job Board
+ AND work progress is tracked

#### Scenario: NPC executes chopping lumber quota
+ GIVEN an NPC has selected a chopping lumber quota with output product (wood) and desired quantity
+ AND trees or wood sources are available nearby
+ AND the Job Board has space available
+ WHEN the NPC works on the quota
+ THEN the NPC performs woodcutting actions
+ AND the NPC chops trees or processes wood
+ AND the NPC produces the output product (lumber or wood items)
+ AND the output product is stored in the Job Board
+ AND work progress is tracked

#### Scenario: NPC executes mining quota
+ GIVEN an NPC has selected a mining quota with output product (copper ore) and desired quantity
+ AND ore deposits are available nearby
+ AND the Job Board has space available
+ WHEN the NPC works on the quota
+ THEN the NPC performs mining actions
+ AND the NPC mines ore deposits
+ AND the NPC produces the output product (copper ore)
+ AND the output product is stored in the Job Board
+ AND work progress is tracked

#### Scenario: NPC executes quota but Job Board is full
+ GIVEN an NPC is working on a quota with an output product and desired quantity
+ AND the NPC has produced some output products
+ AND the Job Board (chest) is full (no space for more output products)
+ AND the quota is incomplete (current quantity < desired quantity)
+ WHEN the NPC attempts to store output products
+ THEN the NPC cannot store the output products in the Job Board
+ AND the NPC wanders around the village
+ AND the NPC displays bragging behavior about their progress (e.g., "how much wood they chopped", "how much ore they mined")
+ AND the NPC continues to hold the quota item
+ AND the NPC may continue working if space becomes available

#### Scenario: NPC completes quota when desired quantity reached
+ GIVEN an NPC is working on a quota with an output product and desired quantity
+ AND the NPC has produced output products
+ AND the current quantity produced reaches the desired quantity
+ WHEN the quota is completed
+ THEN the system detects that the quota is complete (desired quantity reached)
+ AND the quota is marked as complete
+ AND the NPC may receive rewards

### Requirement: Quota work completion
+ The system SHALL detect when a quota is completed.
+ The system SHALL provide rewards or mood bonuses when quotas are completed.
+ The system SHALL allow NPCs to return quota items to Job Boards after completion.
+ The system SHALL allow NPCs to return quota items to their original slot if space is available.
+ The system SHALL handle quota return when Job Board is full (original slot occupied).
+ The system SHALL make NPCs wander near the Job Board and display bragging behavior when they cannot return a completed quota.
+ The system SHALL allow NPCs to seek new quotas after completing one.

#### Scenario: NPC completes quota and returns item
+ GIVEN an NPC is working on a quota with an output product and desired quantity
+ AND the NPC is holding the quota item
+ AND the current quantity produced has reached the desired quantity (quota complete)
+ AND the Job Board has space in the original slot
+ WHEN the quota is completed
+ THEN the system detects completion (desired quantity reached)
+ AND the quota is marked as complete
+ AND the NPC returns the quota item to its original slot in the Job Board
+ AND the NPC may receive mood bonuses or other rewards

#### Scenario: NPC completes quota but Job Board is full (cannot return quota item)
+ GIVEN an NPC is working on a quota with an output product and desired quantity
+ AND the NPC is holding the quota item
+ AND the current quantity produced has reached the desired quantity (quota complete)
+ AND the Job Board is full (original slot is occupied)
+ WHEN the quota is completed
+ THEN the system detects completion (desired quantity reached)
+ AND the quota is marked as complete
+ AND the NPC cannot return the quota item to the Job Board
+ AND the NPC wanders near the Job Board
+ AND the NPC displays bragging behavior about the work they did
+ AND the NPC may receive mood bonuses or other rewards

#### Scenario: NPC receives rewards for completed quota
+ GIVEN an NPC has completed a quota
+ WHEN the quota completion is processed
+ THEN the NPC may receive a mood bonus
+ AND the NPC may receive other rewards (items, experience, etc.)
+ AND rewards are appropriate to the quota type and difficulty

#### Scenario: NPC seeks new quota after completion
+ GIVEN an NPC has completed a quota
+ AND the quota item has been returned (or NPC is bragging if Job Board full)
+ AND the NPC's mood level is still 3 or higher (or has active gold bribe)
+ AND additional quotas are available
+ WHEN the NPC seeks a new quota
+ THEN the NPC can seek a new quota
+ AND the NPC selects and works on the next appropriate quota (scanning from top left, right and down)

### Requirement: Quota work state tracking
+ The system SHALL track which NPCs are working on which quotas.
+ The system SHALL prevent multiple NPCs from working on the same quota item simultaneously.
+ The system SHALL allow duplicate quota items to be created and worked by different NPCs.
+ The system SHALL handle quota work interruptions (NPC mood drops, quota removed, etc.).

#### Scenario: System tracks active quota work
+ GIVEN multiple NPCs are working on quotas
+ WHEN the system tracks quota work state
+ THEN each NPC's active quota is tracked
+ AND quota work progress is tracked
+ AND the system knows which quotas are being worked on
+ AND the system knows which quota items are held by NPCs

#### Scenario: Quota can be worked by one NPC at a time
+ GIVEN a quota item is available in a Job Board
+ AND one NPC is already working on the quota (has taken the quota item)
+ WHEN another NPC attempts to select the same quota item
+ THEN the quota item is marked as in use (held by the first NPC)
+ AND the second NPC cannot select the same quota item
+ AND the second NPC continues scanning (top left, right and down) for another available quota

#### Scenario: Duplicate quotas can be worked by different NPCs
+ GIVEN multiple quota items of the same type exist in a Job Board (duplicates)
+ AND one NPC is working on one quota item
+ WHEN another NPC scans the Job Board
+ THEN the second NPC can select a different quota item of the same type
+ AND both NPCs can work on their respective quota items simultaneously
+ AND each quota item can only be worked by one NPC at a time

#### Scenario: Quota work interrupted by mood drop
+ GIVEN an NPC is working on a quota
+ AND the NPC's mood level drops below 3
+ AND the NPC does not have an active gold bribe
+ WHEN the mood level check fails
+ THEN the NPC stops working on the quota
+ AND the quota becomes available for other NPCs
+ AND the NPC remains idle or wanders until mood level reaches 3 or higher again (or receives gold bribe)

#### Scenario: Quota work continues with gold bribe despite mood drop
+ GIVEN an NPC is working on a quota
+ AND the NPC has an active gold bribe (within 3 days)
+ AND the NPC's mood level drops below 3
+ WHEN the system checks work eligibility
+ THEN the gold bribe check passes
+ AND the NPC continues working on the quota
+ AND the NPC can work regardless of mood level for the duration of the gold bribe
