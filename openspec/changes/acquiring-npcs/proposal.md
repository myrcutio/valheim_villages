# Change Proposal: acquiring-npcs

## Intent

Create an immersive and rewarding system for acquiring NPCs through rescue quests. NPCs are held captive at various locations throughout the world, and players must discover their locations, travel to them, and rescue the NPCs through combat or other challenges. Additionally, when NPCs die, players can recover them through recovery quests that involve retrieving their souls from the underworld. This system:

- Creates emotional attachment and accomplishment when recruiting NPCs
- Provides narrative explanation for why NPCs join the player
- Rewards exploration and combat engagement
- Makes NPC acquisition feel earned rather than automatic
- Allows players to recover lost NPCs through challenging recovery quests

## Scope

- Implement NPC captive locations throughout the world map
- Create discovery mechanisms for finding captive NPC locations
- Implement rescue quest mechanics (combat encounters, puzzle-solving, or other challenges)
- Enable rescued NPCs to join the player's village/town
- Implement NPC loss and recovery mechanics (when NPCs die, players can recover them through recovery quests)
- Integrate with existing villager assignment and management systems
- Out of scope: NPC dialogue systems (separate proposal), post-rescue NPC behavior (covered by other proposals), UI for rescue quests (separate proposal)

## Affected Specs

- `npc-rescue-quests` - NPCs can be rescued from captivity
- `npc-location-discovery` - Players can discover locations of captive NPCs
- `npc-loss-recovery` - Players can recover NPCs that have died through recovery quests

## Existing Code to Modify

*Note: Since this is an early-stage feature, specific code locations will be identified during implementation. Expected areas to modify include:*

- World generation/placement systems - Add captive NPC locations to world
- Exploration/discovery systems - Add mechanisms for discovering NPC locations
- Combat/encounter systems - Add rescue encounters with captors
- NPC spawning/assignment systems - Spawn captive NPCs and handle post-rescue assignment
- Map/marker systems - Display discovered NPC locations

## NPC Types and Locations

NPC types are defined in the `villager-npc-types` proposal. This proposal covers how these NPCs are discovered and rescued. The following lists where each NPC type can be found:

### Villager NPC Types

1. **Farmer** (Villager) - Knowledgeable about agriculture, food production, crop management, and beekeeping
   - Found in: Ruined farms, abandoned villages, or troll caves
   - Captors: Trolls, greydwarves, or other creatures that have overrun settlements

2. **Miner** (Villager) - Specialized in mining, ore extraction, and underground operations
   - Found in: Abandoned mines, mining camps, or goblin camps near ore deposits
   - Captors: Goblins, draugr, or creatures guarding valuable mining resources

3. **Blacksmith** (Villager) - Skilled in metalworking, forging, and weapon/armor repair
   - Found in: Abandoned forges, draugr camps, or goblin strongholds
   - Captors: Draugr, goblins, or hostile creatures guarding the forge

4. **Carpenter** (Villager) - Skilled in woodworking, construction, and toolmaking
   - Found in: Ruined workshops, abandoned construction sites, or goblin camps
   - Captors: Goblins, draugr, or creatures guarding valuable crafting materials

### Specialist NPC Types

5. **Scout** (Specialist) - Provides map information and exploration benefits
   - Found in: Remote outposts, watchtowers, or shipwrecks
   - Captors: Skeleton archers, draugr, or sea creatures

6. **Trader** (Specialist) - Facilitates trade routes between settlements
   - Found in: Abandoned trading posts, merchant camps, or bandit hideouts
   - Captors: Bandits, hostile NPCs, or creatures that have taken over trading routes

7. **Guard** (Specialist) - Provides defense, combat support, and security
   - Found in: Prison camps, military outposts, or draugr fortresses
   - Captors: Draugr warriors, hostile military units, or powerful creatures

8. **Mountaineer** (Specialist) - Specialized in mountain terrain traversal and high-altitude navigation
   - Found in: Mountain outposts, abandoned lodges, or draugr mountain camps
   - Captors: Draugr, stone golems, or creatures guarding mountain passes

9. **Shipwright** (Specialist) - Specialized in shipbuilding, boat repair, and maritime navigation
   - Found in: Shipwrecks, abandoned shipyards, or coastal camps
   - Captors: Sea creatures, draugr, or creatures guarding maritime resources

10. **Tavern Keeper** (Specialist) - Manages tavern operations, cooking, brewing, and provides hospitality services
    - Found in: Abandoned taverns, ruined inns, or bandit camps
    - Captors: Bandits, draugr, or creatures that have taken over hospitality establishments

### Discovery Mechanisms

1. **Map Fragments with Traits** - Players find map fragments with traits that indicate NPC type and location
   - **Fragment Traits**: Each fragment has biome, profession hint, and location type traits
   - **Combination System**: Combining fragments with matching traits reveals specific NPC types
   - **Strategic Targeting**: Players can collect specific fragments to target desired NPC types
   - **Exploration Sources** (encouraging "revealing the map"):
     - **Unveiling New Map Areas**: Fragments discovered when exploring and revealing new map sections
     - **Landmark Discovery**: Fragments found at significant landmarks (ruins, structures, natural features)
     - **Exploration Objectives**: Fragments rewarded for clearing enemy camps, exploring dungeons, reaching high points
     - **Biome Exploration**: Biome-specific fragments found through thorough exploration of each biome
     - **Map Edge Exploration**: Fragments discovered at unexplored map boundaries
     - **High Elevation**: Fragments found at mountains, watchtowers, tall structures
     - **Coastal Exploration**: Fragments discovered along coastlines, islands, shipwrecks
   - **Example**: Combining "Meadows - Forge - Abandoned Settlement" fragments reveals a Blacksmith location
   - **Fragment Types**: Forge → Blacksmith, Farm → Farmer, Mine → Miner, Workshop → Carpenter, Outpost → Scout, Trading Post → Trader, Fortress → Guard, Mountain → Mountaineer, Shipyard → Shipwright, Tavern → Tavern Keeper

2. **Villager Stories (Mead Conversations)** - Players can get villagers drunk with mead and listen to their stories
   - **Mechanic**: Player provides mead to a villager and asks them to tell a specific type of story
   - **Cost**: Villager requires a certain quantity or specific type of mead before telling the story
   - **Reward**: Story reveals NPC location information or provides targeted map fragments
   - **Story Types**: Different story types reveal different NPC types (e.g., "Tell me about blacksmiths" → Blacksmith fragments/location)
   - **Mead Requirements**: 
     - Basic mead → Basic stories (common NPC types)
     - Advanced meads → Advanced stories (rare NPC types or specific locations)
     - Quantity matters: More mead = better/more specific information
   - **Benefits**: 
     - Provides player agency to target specific NPCs
     - Creates humorous social encounters
     - Rewards mead production (connects to Tavern Keeper and Farmer beekeeping)
     - Maintains cost/effort (requires mead, not free)
   - **Example**: Player gives Miner 3 mead and asks "Tell me about blacksmiths" → Miner tells a story that reveals Blacksmith location or provides Blacksmith-specific fragments

3. **Environmental Clues** - Visual or audio cues in the world indicate nearby captives
   - Distant sounds (cries for help, hammering, etc.)
   - Smoke from camps, unusual structures, or signs of recent activity
   - Abandoned equipment or personal belongings that hint at captives

4. **Exploration Rewards** - Discovering certain locations automatically reveals nearby NPC locations
   - Finding a new biome or landmark triggers discovery
   - Completing dungeons or clearing enemy camps reveals information

5. **Quest Chains** - Some NPCs can only be found by following quest chains
   - Rescuing one NPC reveals the location of another
   - Creates interconnected rescue narratives

## Open Questions / Design Decisions Needed

1. **How difficult should rescue encounters be?**
   - **Answer**: Rescue encounters scale by NPC category.
   - **Villager-level quests** (Farmer, Miner, Blacksmith, Carpenter): Roughly equivalent to tomb or crypt level minibosses
   - **Low level specialist NPCs** (Mountaineer, Guard, Scout, Shipwright): Equivalent to black forest or swamp level bosses
   - **High level specialists** (Trader, Tavern Keeper): Equivalent to clearing plains villages or mistlands dverger towers

2. **What happens if a rescue attempt fails? / What happens when a rescued NPC dies?**
   - **Answer (failed rescue attempt)**: If a player dies or otherwise fails to access the NPC captive cage, it remains indefinitely. The player can always come back and attempt to rescue the NPC, it never changes location or despawns.
   - **Answer (rescued NPC death)**: If a player frees an NPC and then that NPC dies, it follows the same rules as reviving an NPC. Their soul will be taken, a gravestone will be spawned where they died, and interacting with it will guide the player to the location of an appropriate level heart quest.

3. **How many NPCs should be available?**
   - **Answer**: NPCs are procedurally generated whenever map fragments are combined to form a complete map. Map fragments can spawn in any chest found out in the world. The fragment type corresponds to the level of the biome:
     - **Villager level fragments** (Farmer, Miner, Blacksmith, Carpenter) only spawn in meadows, black forest, or swamps
     - **Specialist level fragments** (Scout, Trader, Guard, Mountaineer, Shipwright, Tavern Keeper) only spawn in mountain level or higher biomes

4. **Should rescued NPCs have unique personalities or stories?**
   - **Answer**: The NPC has a name, but can be renamed. Initially their only backstory is the log of how the player rescued them, and if they have been revived the story of how they died and how the player revived them.

These questions will be addressed in design.md before implementation begins.
