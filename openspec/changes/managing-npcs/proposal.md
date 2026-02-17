# Change Proposal: managing-npcs

## Intent

Create a comprehensive system for managing NPCs once they have been acquired. This system enables players to assign NPCs to locations, manage their basic needs, interact with them through menus, maintain their mood, and enable them to work on quotas. The system:

- Provides intuitive placement mechanics using quickslots and direct interaction
- Ensures NPCs have basic needs met (beds, food, fire, safety) to prevent stress
- Enables player-NPC interaction through workbench-style menus
- Manages NPC mood through location assignments and temporary bribes
- Introduces a Job Board system for task-based NPC work
- Allows NPCs to autonomously work on quotas when mood requirements are met

## Scope

- Implement NPC placement system (quickslot assignment, placement on beds/workbenches)
- Create NPC basic needs system (beds, food, fire, safe room requirements)
- Implement stressed debuff when basic needs are unmet
- Create NPC interaction menu system (workbench-style interface)
- Implement NPC mood system (mood levels, mood requirements for work)
- Create NPC upgrade system (location assignments, mead/gold bribes with time-based decay)
- Implement Job Board discovery and quota system
- Create NPC quota work system (NPCs seek and work on nearby quotas)
- Enable NPCs to produce items (swaddled villager, map fragment) through interaction
- Out of scope: NPC acquisition mechanics (covered by acquiring-npcs), NPC type definitions (covered by villager-npc-types), NPC dialogue systems (separate proposal), detailed quota task implementations (can be expanded later)

## Affected Specs

- `npc-placement-assignment` (to be created) - NPCs can be placed and assigned to locations
- `npc-basic-needs` (to be created) - NPCs require beds, food, fire, and safety
- `npc-interaction-menu` (to be created) - Players can interact with NPCs through menus
- `npc-mood-system` (to be created) - NPCs have mood levels that affect their ability to work
- `npc-upgrade-system` (to be created) - Players can upgrade NPC conditions through location assignments or bribes
- `job-board-system` (to be created) - Job Board building and quota system
- `npc-quota-work` (to be created) - NPCs can seek and work on quotas

## Existing Code to Modify

*Note: Since this is an early-stage feature, specific code locations will be identified during implementation. Expected areas to modify include:*

- Item/inventory systems - Add NPC items (swaddled villager) that can be placed in quickslots
- Building/placement systems - Modify bed and workbench placement to accept NPC assignments
- NPC/spawn systems - Modify NPC spawning and state management
- UI/menu systems - Add NPC interaction menus (workbench-style interface)
- Debuff/buff systems - Add stressed debuff and mood tracking
- Storage/chest systems - Add Job Board building and quota storage
- AI/behavior systems - Add NPC quota-seeking and work behaviors

## NPC Placement and Assignment

NPCs can be acquired as items (e.g., "swaddled villager" in a crate or carried in back slot). Once acquired:

1. **Quickslot Assignment**: NPCs can be placed in player quickslots (1-9)
2. **Placement on Structures**: Players can use NPCs from quickslots on beds or workbenches to assign them to those locations
3. **Assignment State**: Once assigned, NPCs are associated with the location and become available for interaction

## NPC Basic Needs

NPCs require four basic needs to function properly:

1. **Bed**: NPC must have access to a bed for sleeping
2. **Food**: NPC must have access to food
3. **Fire**: NPC must have access to a fire source (campfire, hearth, etc.)
4. **Safe Room**: NPC must be in a room safe from roaming enemies (enclosed structure)

If any of these needs are unmet, the NPC receives a "stressed" debuff that:
- Prevents the NPC from working
- Prevents the NPC from providing benefits to the player or village
- May affect NPC mood negatively

## NPC Interaction Menu

When players interact with an NPC, a workbench-style menu appears with:

1. **Available Options**: Context-dependent options based on NPC type and unlocked features
   - Initial options: "Hop on!" (mount/ride), "Have a drink with me!" (social interaction)
   - Additional options unlock as NPC mood and conditions improve
2. **Crafting Tab**: Interacting with the main craft menu produces items:
   - "Swaddled villager" (packaged NPC for transport)
   - "Map fragment" (exploration-related items)
   - Other NPC-produced items based on NPC type
3. **Upgrade Tab**: Allows players to upgrade NPC conditions:
   - Assign nearby locations (bed, feast, etc.) to improve NPC mood
   - Bribe NPC with mead or gold for temporary mood boosts
   - View current mood level and requirements

## NPC Mood System

NPCs have a mood level that affects their behavior and ability to work. The mood system works similarly to Valheim's comfort system, where various factors contribute to the mood level:

1. **Mood Level Calculation**: Mood level is calculated from contributing factors (bed, fire, mead, food, etc.), similar to how comfort works
2. **Mood Level Behaviors**:
   - **Level 0**: NPC wanders around crying
   - **Level 1**: NPC wanders around the village
   - **Level 2**: NPC will have conversations with the player
   - **Level 3+**: NPCs will begin seeking out quotas in the village (provided their mood level is sufficient to perform them)
3. **Mood Sources**:
   - **Location Assignments**: Assigning nearby locations (bed, feast, etc.) provides permanent mood improvements
   - **Temporary Bribes**: Providing mead or gold gives temporary mood boosts that decay over time
   - **Basic Needs**: Bed, fire, food, and safety all contribute to mood level
4. **Mood Decay**: 
   - **Mead**: Mood benefits last for one day/night cycle
   - **Gold**: Mood benefits last for 3 days, and gold bribes allow NPCs to work during those 3 days regardless of their mood level
5. **Quota Work Threshold**: NPCs must reach mood level 3 or higher before they will seek and work on quotas (unless they have an active gold bribe, which forces work regardless of mood)

## NPC Upgrade System

Players can improve NPC conditions through two methods:

1. **Location Assignments**: Assign nearby locations to NPCs
   - Assign bed: Provides permanent mood improvement
   - Assign feast: Provides mood improvement and comfort
   - Other location types may be added
2. **Bribes**: Provide temporary mood boosts
   - **Mead**: Provides mood boost that decays over time
   - **Gold**: Provides mood boost that decays over time
   - Different mead types or gold amounts may provide different boost levels
   - Mood benefits wear off gradually over time

## Job Board System

When a player picks up an NPC for the first time, they discover the "Job Board" building:

1. **Discovery**: Job Board is automatically discovered/unlocked when first NPC is acquired
2. **Building Type**: Job Board is a storage chest-like structure
3. **Quota Storage**: Job Board contains quotas (scrolls describing tasks)
4. **Quota Structure**: Each quota contains two parameters:
   - **Output Product**: The item to be produced (e.g., roast boar meat, wood, copper ore)
   - **Desired Quantity**: The amount of the output product to produce
5. **Quota Types**: Quotas describe various tasks NPCs can perform:
   - Cooking food
   - Chopping lumber
   - Patrolling the area
   - Mining resources
   - Crafting items
   - Other daily tasks or chores
6. **Quota System Design**: The quota system is generalized enough that NPCs can perform most any daily task or chore that the player can do

## NPC Quota Work System

NPCs can autonomously work on quotas when conditions are met:

1. **Mood Threshold**: NPCs must reach mood level 3 or higher before seeking quotas (unless they have an active gold bribe, which forces work regardless of mood)
2. **Quota Seeking**: When mood threshold is met, NPCs seek out nearby quotas from Job Boards nearest to their assigned bed
3. **Quota Selection**: NPCs scan Job Boards starting from top left slot, scanning right and down, selecting appropriate quotas based on their type and capabilities
4. **Quota Taking**: NPCs take the quota item while working on it
5. **Work Execution**: NPCs perform the tasks described in the quota, producing the output product specified in the quota
6. **Output Storage**: Output products are stored in the same Job Board (chest) where the quota was found, if possible
7. **Work Progress**: Work progress is tracked (current quantity produced vs. desired quantity)
8. **Bragging Behavior**: 
   - If Job Board is full and quota is incomplete: NPC wanders around village bragging about progress (e.g., "how much wood they chopped", "how much ore they mined")
   - If Job Board is full when quota completes: NPC wanders near Job Board bragging about completed work
9. **Work Completion**: When desired quantity is reached, the quota is complete. NPCs return the quota item to its original slot if space is available
10. **Quota Exclusivity**: Each quota item can only be worked by one NPC at a time, but duplicate quotas can be created

## Open Questions / Design Decisions Needed

1. ~~**What is the exact mood threshold for NPCs to start working?**~~ **RESOLVED**: NPCs require mood level 3+ to seek quotas. Mood level 0 = crying, level 1 = wandering village, level 2 = conversations, level 3+ = quota work.

2. ~~**How long do mead/gold mood benefits last?**~~ **RESOLVED**: Mead lasts for one day/night cycle. Gold lasts for 3 days, and the villager will work during those 3 days regardless of their mood level.

3. ~~**How do NPCs select which quotas to work on?**~~ **RESOLVED**: NPCs search for containers nearest their bed first, and work on quotas starting from the top left slot, scanning right and down. They take the quota item while working and put it back where they found it if possible. If the chest is full and the quota is complete, they wander near the job board bragging about the work they did.

4. ~~**What happens if multiple NPCs want the same quota?**~~ **RESOLVED**: Each quota can only be worked by one NPC at a time, but duplicate quotas can be created.

5. ~~**How detailed should quota task implementations be initially?**~~ **RESOLVED**: A quota initially contains two parameters: an output product (e.g., roast boar meat, wood, copper ore) and a desired quantity. The product will be stored in the same chest where the quota was found, if possible. If the chest is full and the quota is incomplete, the NPC with the quota will wander around the village bragging about their progress (e.g., "how much wood they chopped", "how much ore they mined", etc.).
