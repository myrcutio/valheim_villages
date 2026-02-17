# Design: villager-npc-types

## Approach

This change establishes the foundational system for NPC types in the Valheim Villages mod. It defines a comprehensive catalog of NPC types, each with specific requirements (workbenches, biomes, comfort level, construction materials) and benefits. This system serves as the reference for all other NPC-related features.

The solution focuses on:

1. **NPC Type Definitions**: Clear categorization of NPC types (basic vs. specialized)
2. **Requirement System**: Structured requirements for each NPC type
3. **Benefit System**: Defined benefits each NPC type provides
4. **Validation System**: Mechanisms to check if requirements are met

## Code Modifications

*Specific code locations will be identified during implementation. Expected modifications:*

### NPC Type Definition System
- **Modify NPC/villager data structures**: Add NPC type definitions with requirements and benefits
- **Modify NPC type configuration**: Create data structures for NPC type requirements (workbenches, biomes, comfort, materials)
- **Modify NPC type benefit definitions**: Define what benefits each NPC type provides

### Requirement Validation System
- **Modify workbench detection systems**: Check if required workbenches exist and meet level requirements
- **Modify biome detection systems**: Validate that NPC is in required biome
- **Modify comfort calculation systems**: Check if comfort level meets requirements
- **Modify construction/material systems**: Validate that required construction materials are present

### Benefit Activation System
- **Modify gameplay systems**: Integrate NPC benefits into relevant game systems (crafting, combat, exploration, etc.)
- **Modify benefit tracking**: Track which NPCs are providing which benefits
- **Modify benefit activation**: Enable/disable benefits based on requirement fulfillment

## Alternatives Considered

### 1. Generic NPCs with No Types
- **Rejected**: Lacks depth and strategic choices. NPC types provide meaningful differentiation and player choices.

### 2. Unlimited NPC Types
- **Rejected**: Too many types would be overwhelming and difficult to balance. Focused set provides clear options.

### 3. NPC Types Without Requirements
- **Rejected**: Requirements create strategic depth and reward proper village planning. They also integrate with existing game systems.

### 4. Separate Requirement System (New Code)
- **Rejected**: Violates code modification principle. Should integrate with existing workbench, biome, comfort, and construction systems.

## Implementation Strategy

1. **Identify existing NPC/villager data structures**: Locate where NPCs are defined and stored
2. **Identify existing workbench systems**: Understand how workbenches are tracked and their levels
3. **Identify existing biome detection**: Understand how biomes are determined
4. **Identify existing comfort calculation**: Understand how comfort is calculated in Valheim
5. **Design NPC type data structure**: Define how NPC types, requirements, and benefits are stored
6. **Implement NPC type definitions**: Create definitions for all NPC types with requirements and benefits
7. **Implement requirement validation**: Create system to check if NPC requirements are met
8. **Implement benefit system**: Integrate NPC benefits into relevant game systems
9. **Implement tiered benefit scaling**: Add benefit scaling based on comfort level and workbench level
10. **Implement trade route system**: Add trade route establishment and requirement offset mechanics
11. **Add requirement checking**: Integrate requirement validation into NPC assignment and maintenance systems

## Design Decisions

### NPC Type Categories
- **Villagers**: NPCs that provide indirect benefits through production and resource output. Their effectiveness scales based on the variety of other villagers in the same village or connected settlements. Includes: Farmer, Miner, Blacksmith, Carpenter.
- **Specialists**: NPCs that provide direct gameplay benefits (combat, exploration, navigation, etc.). Their effectiveness scales primarily with comfort level and workbench level. Some Specialists have unique scaling mechanisms (e.g., Tavern Keeper scales with Farmer level). Includes: Scout, Trader, Guard, Mountaineer, Shipwright, Tavern Keeper.

### Requirement Structure
Each NPC type has four requirement categories:
1. **Workbenches**: Required crafting stations and their minimum levels
2. **Biome**: Required biome or biome preferences
3. **Comfort Level**: Minimum comfort rating (uses Valheim's existing comfort system)
4. **Construction Materials**: Required building materials and structures

### Requirement Validation
- Requirements are checked when:
  - NPC is assigned to a location
  - Periodic validation to ensure requirements remain met
  - When player attempts to use NPC benefits
- If requirements are not met:
  - NPC may not provide full benefits
  - NPC may become unavailable for certain functions
  - Player is notified of unmet requirements

### Benefit System
- **Specialist Benefits**: Direct gameplay benefits that are active when:
  - NPC is assigned and active
  - All requirements are met
  - NPC is in appropriate location
- **Villager Benefits**: Production and resource output that:
  - Provides daily resource production
  - Scales with villager variety in the same village or connected settlements
  - Creates interdependence where villagers add levels to each other
- Benefits integrate with existing game systems:
  - Crafting benefits modify crafting systems
  - Combat benefits modify combat systems
  - Exploration benefits modify exploration systems
  - Production benefits provide resources to player inventory or storage

### Villager Interdependence System
- **Level Addition**: Villagers add levels to other villagers based on their profession
  - Example: Farmer provides base level to Miner (food enables mining)
  - Example: Blacksmith adds level to Miner (enables tin production)
  - Example: Carpenter adds levels to all production villagers (better tools)
- **Villager Self-Leveling**: Some villagers can increase their own levels based on conditions
  - Example: Farmer can reach Level 2+ for beekeeping (requires beehives, scales with comfort/variety)
- **Production Scaling**: Villager production capabilities scale with:
  - Base level from supporting villagers (e.g., Farmer → Miner)
  - Additional levels from complementary villagers (e.g., Blacksmith → Miner)
  - Variety of villagers in the same village or connected via trade routes
- **Interdependence Chain Example**:
  - Farmer produces carrots → gives base level to Miner
  - Miner (with Farmer support) produces copper ore
  - Blacksmith adds level to Miner → Miner can now produce tin ore
  - Blacksmith (with Miner producing copper + tin) can produce bronze
  - Carpenter adds levels to all → improves efficiency across the chain
- **Trade Route Integration**: Villagers in connected settlements (via trade routes) can provide levels to each other, enabling distributed production networks

### Tiered Benefit Scaling
- Benefits scale based on comfort level and/or workbench level
- Different NPC types prioritize different scaling factors:
  - **Comfort-based scaling**: NPCs like Guard scale primarily with comfort level (better barracks = better patrols)
  - **Workbench-based scaling**: NPCs like Blacksmith scale primarily with workbench level (higher Forge = better forging)
  - **Combined scaling**: Some NPCs scale with both factors
  - **Villager-dependent scaling**: Some Specialists scale based on Villager levels (e.g., Tavern Keeper scales with Farmer level)
- Benefit tiers provide clear progression:
  - **Tier 1 (Minimum)**: Basic benefits at minimum requirements
  - **Tier 2-3 (Moderate)**: Improved benefits at moderate comfort/workbench levels
  - **Tier 4+ (Advanced)**: Maximum benefits at high comfort/workbench levels
- Example scaling:
  - **Guard**: Comfort 2-3 = small patrol, Meadows enemies; Comfort 10+ = large patrol, Mistlands enemies
  - **Blacksmith**: Forge 1-2, Comfort 3-5 = 10% faster; Forge 5+, Comfort 9+ = 30% faster + quality bonus
  - **Tavern Keeper**: Level 1 (Farmer Level 1) = cook food; Level 2 (Farmer Level 2+) = brew mead; Level 3 (multiple farmers) = feast comfort bonus

### Trade Route System
- **Trader Role**: Traders establish and maintain trade routes between settlements connected by roads
- **Requirement Offsets**: NPCs at settlements connected via trade routes can operate at higher benefit tiers with reduced comfort/workbench requirements
- **Road Benefits**: Makes building roads between villages meaningful and rewarding
- **Use Case - Difficult Biomes**: Helps establish bases in challenging biomes (e.g., Ashlands) where meeting high comfort requirements is difficult
- **Example - Guard in Ashlands**:
  - Without trade route: Guard needs comfort 10+ to prevent Ashlands-level enemy spawns
  - With trade route: Guard can prevent Ashlands-level spawns with comfort 4-6
  - This makes it feasible to establish secure bases in Ashlands without building elaborate high-comfort structures
- **Trade Route Maintenance**: Trade routes require continuous road connections; breaking the road breaks the trade route and removes offsets

## Integration with Existing Systems

This change integrates with:
- **Villager Residency Requirements** (from `add-villager-residency-requirements`): NPC type requirements complement residency requirements
- **Construction Inhabitation** (from `make-villages-useful`): NPC types determine which constructions they can inhabit
- **NPC Acquisition** (from `acquiring-npcs`): NPC types define what NPCs can be rescued
- **Workbench Systems**: Requirement validation uses existing workbench detection
- **Biome Systems**: Requirement validation uses existing biome detection
- **Comfort Systems**: Requirement validation uses existing comfort calculation
- **Construction Systems**: Requirement validation uses existing material and structure detection
- **Road/Infrastructure Systems**: Trade routes require road connections between settlements, making road building meaningful
