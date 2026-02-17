# Change Proposal: villager-npc-types

## Intent

Define a comprehensive and consistent system of NPC types for the Valheim Villages mod. This proposal establishes the foundation for all NPC-related features by defining:

- A clear list of NPC types
- Requirements for each NPC type (workbenches, biomes, comfort level, construction materials)
- Benefits each NPC type provides to the player

This foundational definition ensures consistency across all other proposals that reference NPC types (acquiring-npcs, make-villages-useful, add-villager-residency-requirements) and provides a clear reference for implementation.

## Scope

- Define a comprehensive list of NPC types with clear categorization
- Specify requirements for each NPC type:
  - Required workbenches (Workbench, Forge, Stonecutter, Artisan Table, etc.)
  - Required biomes (Meadows, Black Forest, Swamp, Mountains, Plains, Mistlands, Ashlands)
  - Required comfort level (minimum comfort rating)
  - Required construction materials (wood, stone, iron, etc.)
- Define benefits each NPC type provides to the player
- Define villager interdependence system where villagers add levels to each other
- Define villager production system that scales with villager variety
- Define tiered benefit levels that scale with comfort level or workbench level (for Specialists)
- Create a system for validating NPC requirements
- Out of scope: NPC acquisition mechanics (covered by acquiring-npcs), NPC assignment mechanics (covered by other proposals), NPC behavior implementation (covered by other proposals)

## Affected Specs

- `villager-npc-types` (to be created) - Defines NPC types, requirements, and benefits

## Existing Code to Modify

*Note: Since this is an early-stage feature, specific code locations will be identified during implementation. Expected areas to modify include:*

- NPC/villager data structures - Add NPC type definitions and requirements
- Requirement validation systems - Add NPC type requirement checking
- Workbench/construction systems - Expose workbench and material information for requirement validation
- Biome detection systems - Expose biome information for requirement validation
- Comfort calculation systems - Expose comfort level for requirement validation

## NPC Types and Definitions

### NPC Categories

NPCs are divided into two categories:

1. **Villagers**: Provide indirect benefits through production and resource output. Their effectiveness scales based on the variety of other villagers in the same village (or connected via trade routes). Villagers add levels to each other, creating interdependence.

2. **Specialists**: Provide direct gameplay benefits (combat, exploration, navigation, etc.). Their effectiveness scales primarily with comfort level and workbench level.

### Villager NPC Types

Villagers operate differently from Specialists:
- They provide **production output** (resources, materials, crafted items) rather than direct gameplay benefits
- Their **levels scale based on villager variety** in the same village or connected settlements
- They **add levels to other villagers**, creating interdependence
- Example: A Farmer produces carrots → gives base level to Miner → Miner produces copper ore → Blacksmith adds level to Miner → Miner can produce tin → Blacksmith can produce bronze → Carpenter adds levels to all (better tools improve efficiency)

1. **Farmer** (Villager)
   - **Description**: Knowledgeable about agriculture, food production, crop management, and beekeeping
   - **Required Workbenches**: Workbench (for farm structures)
   - **Required Biome**: Meadows or Plains (prefers fertile land)
   - **Required Comfort Level**: 2 (basic shelter: bed, fire)
   - **Required Construction Materials**: Wood (for farm structures), cultivatable land nearby
   - **Base Production**: 
     - **Level 1**: Produces food items (carrots, turnips, etc.) daily
     - **Level 2+**: Produces honey through beekeeping (requires beehives nearby)
   - **Villager Interdependence**:
     - Provides base level to Miner (food enables mining operations)
     - Receives levels from Carpenter (tools improve farming efficiency)
     - Beekeeping production scales with comfort level and villager variety
   - **Scaling**: Production increases with variety of villagers in village (or connected via trade routes)

2. **Miner** (Villager)
   - **Description**: Specialized in mining, ore extraction, and underground operations
   - **Required Workbenches**: Workbench (for mining camp structures) , Stonecutter (for stonework)
   - **Required Biome**: Black Forest, Mountains, or Swamp (near ore deposits)
   - **Required Comfort Level**: 2 (mining camp: bed, fire)
   - **Required Construction Materials**: Wood (for mining camp structures), proximity to ore deposits
   - **Base Production**: Produces copper ore daily (requires Farmer for base level)
   - **Villager Interdependence**:
     - Requires Farmer (base level) to produce copper ore
     - Receives level from Blacksmith → can produce tin ore
     - Receives level from Carpenter (tools improve mining efficiency)
   - **Scaling**: Production tier increases with supporting villagers (Farmer → copper, Farmer + Blacksmith → tin, etc.)

3. **Blacksmith** (Villager)
   - **Description**: Skilled in metalworking, forging, and weapon/armor repair
   - **Required Workbenches**: Forge (level 1 minimum)
   - **Required Biome**: Any (prefers areas with access to metal resources)
   - **Required Comfort Level**: 3 (basic furniture: bed, fire, table)
   - **Required Construction Materials**: Stone foundation, wood walls, forge equipment
   - **Base Production**: Produces bronze (requires Miner producing tin + copper)
   - **Villager Interdependence**:
     - Requires Miner (copper + tin) to produce bronze
     - Adds level to Miner → enables tin production
     - Receives levels from Carpenter (tools improve forging efficiency)
   - **Scaling**: Production capabilities increase with supporting villagers (Miner → bronze, Miner + Craftsman → advanced alloys, etc.)

4. **Carpenter** (Villager)
   - **Description**: Skilled in woodworking, construction, and toolmaking
   - **Required Workbenches**: Workbench
   - **Required Biome**: Any (prefers areas with diverse resources)
   - **Required Comfort Level**: 3 (workshop: bed, fire, table, workbench area)
   - **Required Construction Materials**: Wood and stone (for workshop structures)
   - **Base Production**: Produces tools and crafted items daily
   - **Villager Interdependence**:
     - Adds levels to Farmer (better tools → better farming)
     - Adds levels to Miner (better tools → better mining)
     - Adds levels to Blacksmith (better tools → better forging)
   - **Scaling**: Tool quality and production increases with variety of villagers served

### Specialist NPC Types

Specialists provide direct gameplay benefits that scale with comfort level and workbench level. Some Specialists (like Tavern Keeper) have unique scaling mechanisms based on Villager levels.

1. **Scout**
   - **Description**: Provides exploration benefits, map information, and navigation assistance
   - **Required Workbenches**: Workbench (for outpost structures)
   - **Required Biome**: Any (prefers elevated or remote locations)
   - **Required Comfort Level**: 2 (basic shelter: bed, fire)
   - **Required Construction Materials**: Wood (for watchtower/outpost structures)
   - **Benefits** (scaled by comfort level):
     - **Comfort 2-3**: Basic exploration bonuses (reduced stamina drain, better visibility), reveals nearby points of interest
     - **Comfort 4-6**: Wider discovery radius on world map, marks resource locations and enemy camps
     - **Comfort 7-9**: Marks nearby dungeons and spawners on map, 1.5x wisplight radius improvement (in Mistlands)
     - **Comfort 10+**: Maximum discovery radius, marks all nearby dungeons/spawners, 2x wisplight radius improvement (in Mistlands)

2. **Trader**
   - **Description**: Facilitates trade routes between settlements, providing connectivity benefits and requirement offsets
   - **Required Workbenches**: Workbench (for trading post structures)
   - **Required Biome**: Any (prefers locations near roads or trade routes)
   - **Required Comfort Level**: 4 (comfortable trading post: bed, fire, table, decorations)
   - **Required Construction Materials**: Wood and stone (for trading post structures), connection to road network
   - **Benefits** (scaled by trade route connectivity):
     - **Trade Route Establishment**: Creates trade routes between settlements connected by roads
     - **Requirement Offset**: NPCs at connected settlements can operate at higher tiers with reduced comfort/workbench requirements
     - **Example - Guard in Ashlands**: A guard connected via trade route can prevent Ashlands-level enemy spawns even with lower comfort (comfort 4-6 instead of 10+)
     - **Resource Exchange**: Resources at connected settlements may be shared when building or crafting.

3. **Guard**
   - **Description**: Provides defense, combat support, and security
   - **Required Workbenches**: Workbench (for barracks/defensive structures)
   - **Required Biome**: Any (prefers strategic defensive positions)
   - **Required Comfort Level**: 2 (barracks: bed, fire)
   - **Required Construction Materials**: Wood and stone (for defensive structures, walls)
   - **Benefits** (scaled by comfort level):
     - **Comfort 2-3**: Small patrol area, prevents Meadows-level enemy spawns
     - **Comfort 4-6**: Medium patrol area, prevents Black Forest-level enemy spawns
     - **Comfort 7-9**: Large patrol area, prevents Swamp/Mountains-level enemy spawns
     - **Comfort 10-14**: Very large patrol area, prevents Plains/Mistlands-level enemy spawns
     - **Comfort 14+**: Very large patrol area (equivalent to 2x shield generator range), prevents Ashlands-level enemy spawns.  Requires connected high-level trader.
     - Defends village/constructions from enemies
     - Combat support during player engagements
     - Patrols and security monitoring

4. **Mountaineer**
   - **Description**: Specialized in mountain terrain traversal and high-altitude navigation
   - **Required Workbenches**: Forge (for climbing equipment)
   - **Required Biome**: Mountains
   - **Required Comfort Level**: 4 (mountain lodge: bed, fire, table, lox pelt rug)
   - **Required Construction Materials**: Stone (for mountain lodge), lox pelt (for rug), forge equipment
   - **Benefits**:
     - Improved traction and mobility on steep surfaces
     - Reduced stamina drain when climbing
     - Better combat stability on slopes
     - Particularly useful in Mistlands terrain

5. **Shipwright**
   - **Description**: Specialized in shipbuilding, boat repair, and maritime navigation
   - **Required Workbenches**: Workbench (for shipyard structures)
   - **Required Biome**: Coastal (any biome with water access)
   - **Required Comfort Level**: 3 (shipyard: bed, fire, table)
   - **Required Construction Materials**: Wood (for shipyard structures), access to deep water
   - **Benefits** (scaled by comfort level):
     - **Comfort 3-5**: Improved sailing speed (10% faster), better wind handling
     - **Comfort 6-8**: Enhanced sailing speed (20% faster), can tack into wind at more angles
     - **Comfort 9+**: Maximum sailing speed (30% faster), excellent wind handling, smaller turning radius
     - Ship repair services (jury-rig buff that can repair any ship up to half health once every 20 minutes)

6. **Tavern Keeper**
   - **Description**: Manages tavern operations, cooking, brewing, and provides hospitality services
   - **Required Workbenches**: Food preparation table, Cauldron, Iron roasting spit (all three required for residency)
   - **Required Biome**: Any (prefers locations near settlements or trade routes)
   - **Required Comfort Level**: 4 (tavern: bed, fire, table, decorations)
   - **Required Construction Materials**: Wood and stone (for tavern structures), food preparation table, cauldron, iron roasting spit
   - **Residency Requirements**: Tavern Keeper requires all three workbenches (food preparation table, cauldron, iron roasting spit) to be considered a resident of the village
   - **Benefits** (scaled by Farmer level in same village or connected via trade route):
     - **Level 1** (Farmer Level 1): Can cook basic food items (follows normal cauldron/food preparation table level requirements)
     - **Level 2** (Farmer Level 2+ with beekeeping): Can brew mead in addition to cooking food (follows normal cauldron level requirements)
     - **Level 3** (Farmer Level 2+ with multiple farmers or high variety): Can provide additional comfort bonus when assigned to monitor feasts
     - **Unique Scaling**: Tavern Keeper level is directly based on Farmer level(s) in the same village or connected settlements
     - **Feast Monitoring**: When assigned to monitor a feast, provides additional comfort bonus to all players/NPCs using the feast


## Open Questions / Design Decisions Needed

1. **Should NPC types have tiered requirements?**
   - Basic requirements for initial assignment vs. enhanced requirements for full benefits?
   - Or single set of requirements that must all be met?
   - **Decision**: NPC types will have basic requirements, with that NPC's benefits scaling with either comfort, workbench level, NPC variety in the town, or resources available to a connected trader.

2. **How should comfort level be calculated?**
   - Use existing Valheim comfort system?
   - Custom calculation for NPC requirements?
   - Should different NPC types value different comfort sources?
   - **Decision**: Villagers require a bed, and the comfort level is calculated based on nearby comfort sources at the head of the bed.

3. **Should NPC types have progression or upgrades?**
   - ~~Can NPCs improve their capabilities over time?~~
   - ~~Or are benefits fixed based on NPC type?~~
   - **Decision**: Benefits scale with comfort level and workbench level (tiered system)

4. **How should requirement validation work?**
   - Real-time checking vs. periodic validation?
   - What happens when requirements are no longer met?
   - **Decision**: Validation happens when a player requests a buff or when a comfort or workbench is created/destroyed.  Only NPCs with beds nearby need be recalculated.  A player only loses their chosen buff if they die or if they choose to switch it.

5. **How should benefit scaling work?**
   - Scale based on comfort level, workbench level, or both?
   - How many tiers should each NPC type have?
   - **Decision**: Benefits scale with both comfort level and workbench level, with different NPC types prioritizing different scaling factors

These questions will be addressed in design.md before implementation begins.
