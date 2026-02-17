# Design: acquiring-npcs

## Approach

This change implements a rescue quest system where NPCs are held captive at various locations throughout the world. Players must discover these locations through exploration and clues, then rescue the NPCs through combat or other challenges. The system creates emotional investment and narrative depth while rewarding exploration and combat.

The solution is broken into two atomic capabilities:

1. **NPC Location Discovery**: Players can discover where captive NPCs are located through various mechanisms
2. **NPC Rescue Quests**: Players can rescue captive NPCs from their captors, after which NPCs join the player's village

## Code Modifications

*Specific code locations will be identified during implementation. Expected modifications:*

### NPC Location Discovery System
- **Modify world generation/placement systems**: Place captive NPC locations during world generation or dynamically
- **Modify exploration/discovery systems**: Add discovery mechanisms (map fragments, rumors, environmental clues)
- **Modify map/marker systems**: Display discovered NPC locations on the map
- **Modify item/loot systems**: Add map fragments and clues as discoverable items

### NPC Rescue Quest System
- **Modify NPC spawning systems**: Spawn captive NPCs at designated locations with captor entities
- **Modify combat/encounter systems**: Create rescue encounters with appropriate difficulty
- **Modify quest/objective systems**: Track rescue quest state (discovered, in progress, completed)
- **Modify NPC assignment systems**: Handle post-rescue NPC assignment to player's village

## Alternatives Considered

### 1. Automatic NPC Spawning
- **Rejected**: Doesn't create attachment or accomplishment. Players should earn NPCs through effort.

### 2. NPCs Spawn at Player's Base
- **Rejected**: Removes exploration incentive and narrative depth. No sense of rescue or discovery.

### 3. Simple Purchase System
- **Rejected**: Lacks emotional investment and narrative. Doesn't reward exploration or combat.

### 4. Random NPC Encounters
- **Considered**: NPCs found randomly while exploring
- **Rejected**: Less structured than rescue quests. Doesn't create the same sense of accomplishment or narrative.

### 5. Separate Discovery System (New Code)
- **Rejected**: Violates code modification principle. Should integrate with existing exploration and quest systems.

## Implementation Strategy

1. **Identify existing world generation code**: Locate where structures and entities are placed in the world
2. **Identify existing exploration/discovery systems**: Understand how players discover locations and points of interest
3. **Design captive NPC location system**: Define how NPC locations are placed and tracked
4. **Implement discovery mechanisms**: Add map fragments, rumors, environmental clues, and exploration rewards
5. **Design rescue encounter system**: Define combat encounters and challenges for rescuing NPCs
6. **Implement rescue quest mechanics**: Create rescue encounters with captors, track quest state
7. **Integrate with NPC assignment**: Connect rescued NPCs to existing villager assignment systems
8. **Add map markers**: Display discovered NPC locations on player's map

## Design Decisions

### NPC Location Placement
- NPC locations are placed during world generation or dynamically as players explore
- Locations are biome-appropriate (e.g., blacksmiths in areas with forges, farmers in agricultural areas)
- Locations are distributed across different biomes to encourage exploration
- Some NPCs may be placed in challenging or remote locations to reward advanced players

### Discovery Mechanisms Priority
Initial implementation focuses on:
1. **Map Fragments with Traits** - Primary discovery mechanism, found through exploration and "revealing the map"
2. **Villager Stories (Mead Conversations)** - Secondary mechanism, provides player agency to target specific NPCs
3. **Environmental Clues** - Tertiary mechanism, provides hints about nearby captives
4. **Exploration Rewards** - Integrated with fragment system, rewards discovering new areas

Additional mechanisms (quest chains) can be added in future iterations.

### Villager Stories (Mead Conversations) System
- **Mechanic**: Players provide mead to villagers and request specific story types
- **Cost System**: 
  - Different story types require different mead types or quantities
  - Basic stories (common NPCs) require basic mead
  - Advanced stories (rare NPCs or specific locations) require advanced meads or larger quantities
- **Story Types**: 
  - Players can ask for stories about specific NPC types (e.g., "Tell me about blacksmiths")
  - Stories reveal NPC location information or provide targeted map fragments
  - Different villagers may know different stories (e.g., Miner might know more about Blacksmith stories)
- **Rewards**:
  - Stories provide NPC location hints or fragments
  - More mead = better/more specific information
  - Creates humorous social encounters while providing gameplay value
- **Integration**:
  - Connects to Tavern Keeper (mead production)
  - Connects to Farmer beekeeping (honey for mead)
  - Provides player agency without removing random discovery excitement
  - Maintains cost/effort (requires mead production, not free)

### Map Fragment System Design
**Fragment Traits:**
- **Biome**: Indicates which biome the NPC is located in (Meadows, Black Forest, Mountains, Plains, etc.)
- **Profession Hint**: Suggests NPC type (Forge → Blacksmith, Farm → Farmer, Mine → Miner, Workshop → Carpenter, Outpost → Scout, Trading Post → Trader, Fortress → Guard, Mountain → Mountaineer, Shipyard → Shipwright, Tavern → Tavern Keeper)
- **Location Type**: Describes the location (Abandoned Settlement, Ruins, Fortress, etc.)

**Fragment Combination:**
- Players combine fragments with matching or complementary traits
- Combination determines which NPC type is revealed
- Example: Combining "Meadows - Forge - Abandoned Settlement" fragments reveals a Blacksmith location in Meadows
- Strategic element: Players can target specific NPC types by collecting the right fragments

**Fragment Sources (Exploration-Focused):**
- **Unveiling New Map Areas**: Fragments discovered when revealing unexplored map sections
- **Landmark Discovery**: Fragments found at significant landmarks (ruins, structures, natural features)
- **Exploration Objectives**: Fragments rewarded for completing exploration goals (clearing camps, exploring dungeons)
- **Biome Exploration**: Biome-specific fragments found through thorough exploration of each biome
- **Map Edge Exploration**: Fragments discovered at map boundaries, encouraging expansion
- **High Elevation**: Fragments found at high vantage points (mountains, watchtowers)
- **Coastal Exploration**: Fragments discovered along coastlines, islands, shipwrecks

This system directly rewards "revealing the map" and encourages comprehensive exploration across all biomes and terrain types.

### Rescue Encounter Design
- Rescue encounters scale with the NPC type and location difficulty
- Encounters involve combat with captors (draugr, goblins, trolls, etc.)
- Some NPCs may require puzzle-solving or other non-combat challenges
- Failed rescue attempts allow retry (NPC remains captive)
- Successful rescue immediately makes NPC available for assignment

### NPC Types and Specialization
NPC types are defined in the `villager-npc-types` proposal. This proposal covers discovery and rescue mechanics for all NPC types:

**Villager NPC Types** (provide production output):
- **Farmer**: Food production, agriculture knowledge, beekeeping
- **Miner**: Ore extraction and mining operations
- **Blacksmith**: Metalworking and forging
- **Carpenter**: Woodworking, construction, and toolmaking

**Specialist NPC Types** (provide direct gameplay benefits):
- **Scout**: Map information, exploration bonuses
- **Trader**: Trade routes, requirement offsets
- **Guard**: Defense capabilities, combat support
- **Mountaineer**: Terrain traversal improvements
- **Shipwright**: Sailing speed and wind handling
- **Tavern Keeper**: Cooking, brewing, feast comfort bonuses

*Specific NPC benefits will be refined during implementation based on gameplay balance.*

## Integration with Existing Systems

This change integrates with:
- **Villager Residency Requirements** (from `add-villager-residency-requirements`): Rescued NPCs must meet residency requirements to join villages
- **Construction Inhabitation** (from `make-villages-useful`): Rescued NPCs can be assigned to inhabit constructions
- **NPC Assignment Systems**: Rescued NPCs become available for assignment to villages/constructions
- **Exploration Systems**: Discovery mechanisms leverage existing exploration mechanics
- **Combat Systems**: Rescue encounters use existing combat mechanics
