# Design: managing-npcs

## Approach

This change implements a comprehensive NPC management system that enables players to place, assign, and manage NPCs after acquisition. The system focuses on creating meaningful interactions between players and NPCs while enabling NPCs to work autonomously on tasks.

The solution is broken into seven atomic capabilities:

1. **NPC Placement and Assignment**: NPCs can be placed in quickslots and assigned to beds/workbenches
2. **NPC Basic Needs**: NPCs require beds, food, fire, and safety to avoid stress
3. **NPC Interaction Menu**: Players interact with NPCs through workbench-style menus
4. **NPC Mood System**: NPCs have mood levels that affect their ability to work
5. **NPC Upgrade System**: Players can improve NPC conditions through location assignments or bribes
6. **Job Board System**: Job Board building and quota storage system
7. **NPC Quota Work**: NPCs seek and work on quotas when mood requirements are met

## Code Modifications

*Specific code locations will be identified during implementation. Expected modifications:*

### NPC Placement and Assignment System
- **Modify item/inventory systems**: Add NPC items (swaddled villager) that can be placed in quickslots
- **Modify building interaction systems**: Add NPC assignment logic to beds and workbenches
- **Modify NPC state management**: Track NPC assignment state and location associations

### NPC Basic Needs System
- **Modify NPC state tracking**: Track NPC basic needs (bed, food, fire, safety)
- **Modify debuff systems**: Add "stressed" debuff when needs are unmet
- **Modify building detection**: Check for nearby beds, food sources, fires, and safe structures
- **Modify enemy detection**: Determine if NPC location is safe from roaming enemies

### NPC Interaction Menu System
- **Modify UI/menu systems**: Create workbench-style menu for NPC interaction
- **Modify crafting systems**: Add NPC-produced items (swaddled villager, map fragment) to crafting menus
- **Modify menu state management**: Track available options based on NPC type and unlocked features

### NPC Mood System
- **Modify NPC state tracking**: Add mood level tracking to NPCs
- **Modify mood calculation**: Calculate mood based on location assignments and temporary bribes
- **Modify mood decay**: Implement time-based decay for temporary mood benefits
- **Modify work eligibility**: Check mood threshold before allowing quota work

### NPC Upgrade System
- **Modify location assignment**: Allow players to assign nearby locations (bed, feast) to NPCs
- **Modify bribe mechanics**: Handle mead/gold bribes and temporary mood boosts
- **Modify mood tracking**: Track temporary mood benefits and decay timers

### Job Board System
- **Modify building systems**: Add Job Board building type (storage chest-like)
- **Modify discovery systems**: Unlock Job Board when first NPC is acquired
- **Modify quota storage**: Create quota item type (scrolls) and storage system
- **Modify quota definitions**: Create generalized quota system for task descriptions

### NPC Quota Work System
- **Modify NPC AI/behavior**: Add quota-seeking behavior when mood threshold is met
- **Modify quota selection**: Implement quota selection logic based on NPC type and proximity
- **Modify work execution**: Implement task execution for various quota types
- **Modify work completion**: Handle quota completion and rewards

## Alternatives Considered

### 1. Simple Assignment Without Needs
- **Rejected**: Lacks depth and strategic management. Basic needs create meaningful choices and resource management.

### 2. Fixed NPC Locations
- **Rejected**: Limits player agency. Quickslot placement provides flexibility and intuitive interaction.

### 3. Manual Task Assignment
- **Rejected**: Too micromanagement-heavy. Autonomous quota work reduces player burden while maintaining strategic choices.

### 4. Separate Menu System
- **Rejected**: Workbench-style menu provides familiar interface and integrates with existing crafting systems.

### 5. Permanent Mood Boosts Only
- **Rejected**: Temporary bribes add resource management depth and create interesting trade-offs.

## Design Decisions

### NPC Placement Mechanics
- NPCs are items that can be placed in quickslots for easy access
- Placement on beds/workbenches assigns NPCs to those locations
- Assignment creates a persistent association between NPC and location

### Basic Needs System
- Four core needs: bed, food, fire, safety
- All needs must be met to avoid stressed debuff
- Stressed debuff prevents all work and benefits
- Needs are checked periodically and on interaction

### Interaction Menu Design
- Workbench-style interface for familiarity
- Context-dependent options based on NPC state
- Crafting tab produces NPC-specific items
- Upgrade tab manages mood and conditions

### Mood System Design
- Numeric mood level (integer levels, similar to comfort system)
- Mood level calculated from contributing factors (bed, fire, mead, food, etc.), similar to how comfort works
- Mood level behaviors:
  - Level 0: NPC wanders around crying
  - Level 1: NPC wanders around the village
  - Level 2: NPC can have conversations with the player
  - Level 3+: NPCs can seek and work on quotas
- Location assignments provide permanent mood improvements
- Temporary bribes (mead/gold) provide time-limited boosts:
  - **Mead**: Lasts for one day/night cycle
  - **Gold**: Lasts for 3 days, and allows NPCs to work on quotas regardless of mood level during those 3 days
- Mood decay: Mead expires after one day/night cycle, gold expires after 3 days

### Upgrade System Design
- Location assignments: Permanent improvements through nearby structures
- Bribes: Temporary improvements through resource expenditure
- Different mead types or gold amounts provide different boost levels
- Mood benefits decay gradually, not instantly

### Job Board Discovery
- Discovered automatically when first NPC is acquired
- Storage chest-like structure for quota management
- Quotas are scroll items describing tasks
- System is generalized for expandability

### Quota Work System
- NPCs seek quotas when mood threshold is met (or with active gold bribe)
- NPCs prioritize Job Boards nearest to their assigned bed
- Quota selection: NPCs scan Job Boards starting from top left slot, scanning right and down
- Quota structure: Each quota contains two parameters - an output product (e.g., roast boar meat, wood, copper ore) and a desired quantity
- NPCs take quota items while working and return them to original slot after completion
- Output products are stored in the same Job Board (chest) where the quota was found, if possible
- Work progress is tracked (current quantity produced vs. desired quantity)
- If Job Board is full and quota is incomplete: NPC wanders around village bragging about progress (e.g., "how much wood they chopped", "how much ore they mined")
- If Job Board is full when quota completes: NPC wanders near Job Board bragging about completed work
- Each quota item can only be worked by one NPC at a time, but duplicate quotas can be created
- Work execution follows quota task descriptions
- System supports most player-available tasks

## Integration with Existing Systems

This change integrates with:
- **NPC Acquisition** (from `acquiring-npcs`): NPCs acquired through rescue quests become manageable
- **NPC Types** (from `villager-npc-types`): NPC types determine available options and quota capabilities
- **Building Systems**: Beds, workbenches, and other structures are used for NPC assignment
- **Crafting Systems**: NPC-produced items integrate with existing crafting menus
- **Debuff/Buff Systems**: Stressed debuff and mood system use existing buff/debuff infrastructure
- **Storage Systems**: Job Board uses existing storage/chest systems
- **AI/Behavior Systems**: Quota work extends existing NPC behavior systems
