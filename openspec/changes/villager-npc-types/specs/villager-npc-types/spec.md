# villager-npc-types - Spec Delta

## ADDED Requirements

### Requirement: NPC type definitions
+ The system SHALL support multiple NPC types organized into two categories: Villagers (Farmer, Miner, Blacksmith, Carpenter) and Specialists (Scout, Trader, Guard, Mountaineer, Shipwright, Tavern Keeper).
+ The system SHALL define requirements for each NPC type (workbenches, biome, comfort level, construction materials).
+ The system SHALL define benefits for each NPC type (production output for Villagers, direct gameplay benefits for Specialists).

#### Scenario: NPC type has defined requirements
+ GIVEN an NPC type is defined (e.g., Blacksmith)
+ WHEN the system queries the NPC type requirements
+ THEN the system returns required workbenches (Forge level 1)
+ AND returns required biome (any)
+ AND returns required comfort level (3)
+ AND returns required construction materials (stone foundation, wood walls, forge equipment)

#### Scenario: NPC type has defined benefits
+ GIVEN an NPC type is defined (e.g., Blacksmith)
+ WHEN the system queries the NPC type benefits
+ THEN the system returns the benefits provided by that NPC type
+ AND benefits are clearly defined (e.g., enhanced forging, equipment repair, advanced recipes)

### Requirement: Workbench requirements
+ The system SHALL validate that NPCs have access to required workbenches.
+ The system SHALL check workbench level requirements (minimum level needed).
+ The system SHALL track which workbenches are available for requirement validation.

#### Scenario: Blacksmith requires Forge
+ GIVEN a Blacksmith NPC is assigned to a location
+ WHEN workbench requirements are validated
+ THEN the system checks if a Forge exists at the location
+ AND the system checks if the Forge is at least level 1
+ AND validation passes only if both conditions are met

#### Scenario: Carpenter requires multiple workbenches
+ GIVEN a Carpenter NPC is assigned to a location
+ WHEN workbench requirements are validated
+ THEN the system checks if both Workbench and Stonecutter exist
+ AND validation passes only if both workbenches are present

#### Scenario: Tavern Keeper requires food preparation equipment
+ GIVEN a Tavern Keeper NPC is assigned to a location
+ WHEN workbench requirements are validated
+ THEN the system checks if Food preparation table exists
+ AND the system checks if Cauldron exists
+ AND the system checks if Iron roasting spit exists
+ AND validation passes only if all three workbenches are present
+ AND Tavern Keeper residency requires all three workbenches

#### Scenario: Workbench requirement not met
+ GIVEN an NPC requires a specific workbench
+ AND the required workbench is not present at the location
+ WHEN workbench requirements are validated
+ THEN validation fails
+ AND the unmet requirement is identified
+ AND the player is notified of the missing workbench

### Requirement: Biome requirements
+ The system SHALL validate that NPCs are in required biomes.
+ The system SHALL support required biomes (must be in specific biome) and preferred biomes (works in any, but prefers specific).
+ The system SHALL track current biome for requirement validation.

#### Scenario: Mountaineer requires Mountains biome
+ GIVEN a Mountaineer NPC is assigned to a location
+ WHEN biome requirements are validated
+ THEN the system checks if the location is in the Mountains biome
+ AND validation passes only if the location is in Mountains biome

#### Scenario: Farmer prefers Meadows or Plains
+ GIVEN a Farmer NPC is assigned to a location
+ WHEN biome requirements are validated
+ THEN the system checks if the location is in Meadows or Plains biome
+ AND validation passes if the location is in either preferred biome
+ AND validation may fail or provide reduced benefits if in other biomes

#### Scenario: Blacksmith works in any biome
+ GIVEN a Blacksmith NPC is assigned to a location
+ WHEN biome requirements are validated
+ THEN the system accepts any biome
+ AND validation passes regardless of biome

### Requirement: Comfort level requirements
+ The system SHALL validate that NPC locations meet minimum comfort level requirements.
+ The system SHALL use existing Valheim comfort calculation systems.
+ The system SHALL track comfort level for requirement validation.

#### Scenario: Trader requires comfort level 4
+ GIVEN a Trader NPC is assigned to a location
+ WHEN comfort level requirements are validated
+ THEN the system calculates the comfort level at the location
+ AND the system checks if comfort level is at least 4
+ AND validation passes only if comfort level meets or exceeds 4

#### Scenario: Guard requires comfort level 2
+ GIVEN a Guard NPC is assigned to a location
+ WHEN comfort level requirements are validated
+ THEN the system calculates the comfort level at the location
+ AND the system checks if comfort level is at least 2
+ AND validation passes only if comfort level meets or exceeds 2

#### Scenario: Comfort requirement not met
+ GIVEN an NPC requires a specific comfort level
+ AND the location's comfort level is below the requirement
+ WHEN comfort level requirements are validated
+ THEN validation fails
+ AND the unmet requirement is identified
+ AND the player is notified of insufficient comfort level

### Requirement: Construction material requirements
+ The system SHALL validate that NPC locations have required construction materials and structures.
+ The system SHALL check for specific building materials (wood, stone, etc.).
+ The system SHALL check for specific structures or building types.

#### Scenario: Blacksmith requires stone foundation and forge equipment
+ GIVEN a Blacksmith NPC is assigned to a location
+ WHEN construction material requirements are validated
+ THEN the system checks if stone foundation exists
+ AND the system checks if forge equipment is present
+ AND validation passes only if both requirements are met

#### Scenario: Mountaineer requires lox pelt rug
+ GIVEN a Mountaineer NPC is assigned to a location
+ WHEN construction material requirements are validated
+ THEN the system checks if a lox pelt rug is present in the construction
+ AND validation passes only if the rug is present

#### Scenario: Material requirement not met
+ GIVEN an NPC requires specific construction materials
+ AND the required materials or structures are not present
+ WHEN construction material requirements are validated
+ THEN validation fails
+ AND the unmet requirement is identified
+ AND the player is notified of missing materials or structures

### Requirement: Comprehensive requirement validation
+ The system SHALL validate all requirement categories (workbenches, biome, comfort, materials) together.
+ The system SHALL provide detailed status of which requirements are met and unmet.
+ The system SHALL trigger validation at appropriate times (assignment, periodic checks, benefit use).

#### Scenario: All requirements met
+ GIVEN an NPC is assigned to a location
+ WHEN comprehensive requirement validation is performed
+ AND all workbench requirements are met
+ AND all biome requirements are met
+ AND all comfort level requirements are met
+ AND all construction material requirements are met
+ THEN validation passes
+ AND the NPC is fully operational
+ AND all benefits are available

#### Scenario: Some requirements unmet
+ GIVEN an NPC is assigned to a location
+ WHEN comprehensive requirement validation is performed
+ AND some requirements are met
+ AND some requirements are unmet
+ THEN validation provides detailed status
+ AND identifies which requirements are met
+ AND identifies which requirements are unmet
+ AND NPC benefits may be limited or unavailable

#### Scenario: Requirement validation on assignment
+ GIVEN a player attempts to assign an NPC to a location
+ WHEN assignment is initiated
+ THEN comprehensive requirement validation is performed
+ AND assignment proceeds only if all requirements are met
+ OR assignment proceeds with warnings if some requirements are unmet

### Requirement: NPC type benefits
+ The system SHALL provide benefits based on NPC type when requirements are met.
+ The system SHALL activate benefits when NPC is assigned and requirements are satisfied.
+ The system SHALL deactivate benefits when requirements are no longer met.

#### Scenario: Blacksmith provides forging benefits
+ GIVEN a Blacksmith NPC is assigned to a location
+ AND all requirements are met
+ WHEN the player uses forging systems
+ THEN Blacksmith benefits are active
+ AND forging is enhanced (faster smelting, improved quality)
+ AND equipment repair services are available
+ AND advanced metalworking recipes are accessible

#### Scenario: Scout provides exploration benefits
+ GIVEN a Scout NPC is assigned to a location
+ AND all requirements are met
+ WHEN the player explores the world
+ THEN Scout benefits are active
+ AND nearby points of interest are revealed on map
+ AND exploration bonuses are applied (reduced stamina drain, better visibility)
+ AND resource locations and enemy camps are marked

#### Scenario: Scout benefits scale with comfort level
+ GIVEN a Scout NPC is assigned to a location
+ AND the location has comfort level 2-3
+ WHEN Scout benefits are active
+ THEN basic exploration bonuses are provided (reduced stamina drain, better visibility)
+ AND nearby points of interest are revealed on map

#### Scenario: Scout provides enhanced map discovery
+ GIVEN a Scout NPC is assigned to a location
+ AND the location has comfort level 4-6
+ WHEN Scout benefits are active
+ THEN wider discovery radius on world map is provided
+ AND resource locations and enemy camps are marked on map

#### Scenario: Scout marks dungeons and improves wisplight
+ GIVEN a Scout NPC is assigned to a location
+ AND the location has comfort level 7-9
+ WHEN Scout benefits are active
+ THEN nearby dungeons and spawners are marked on map
+ AND wisplight radius is improved by 1.5x (in Mistlands biome)

#### Scenario: Scout provides maximum exploration benefits
+ GIVEN a Scout NPC is assigned to a location
+ AND the location has comfort level 10+
+ WHEN Scout benefits are active
+ THEN maximum discovery radius on world map is provided
+ AND all nearby dungeons and spawners are marked on map
+ AND wisplight radius is improved by 2x (in Mistlands biome)

#### Scenario: Mountaineer provides terrain benefits
+ GIVEN a Mountaineer NPC is assigned to a location in Mountains biome
+ AND all requirements are met
+ WHEN the player traverses steep terrain (especially in Mistlands)
+ THEN Mountaineer benefits are active
+ AND player has improved traction and mobility on steep surfaces
+ AND stamina drain when climbing is reduced
+ AND combat stability on slopes is improved

#### Scenario: Shipwright provides sailing benefits
+ GIVEN a Shipwright NPC is assigned to a coastal location
+ AND all requirements are met
+ WHEN the player sails a ship
+ THEN Shipwright benefits are active
+ AND sailing speed is improved
+ AND wind handling is enhanced (can tack into wind at wider angles)
+ AND ship repair services are available

#### Scenario: Benefits deactivate when requirements unmet
+ GIVEN an NPC is providing benefits
+ AND requirements are no longer met (e.g., workbench destroyed, comfort decreased)
+ WHEN requirement validation occurs
+ THEN benefits are deactivated
+ AND the player is notified that benefits are unavailable
+ AND benefits reactivate when requirements are restored

### Requirement: Tiered benefit scaling
+ The system SHALL scale NPC benefits based on comfort level and/or workbench level.
+ The system SHALL provide multiple benefit tiers for each NPC type.
+ The system SHALL determine benefit tier based on current comfort level and workbench level.

#### Scenario: Guard benefits scale with comfort level
+ GIVEN a Guard NPC is assigned to a location
+ AND the location has comfort level 2-3
+ WHEN Guard benefits are active
+ THEN Guard provides small patrol area
+ AND Guard prevents spawns of Meadows-level enemies only
+ AND basic security monitoring is available

#### Scenario: Guard benefits scale to high comfort
+ GIVEN a Guard NPC is assigned to a location
+ AND the location has comfort level 10 or higher (high quality barracks)
+ WHEN Guard benefits are active
+ THEN Guard provides very large patrol area
+ AND Guard prevents spawns of Plains/Mistlands-level enemies
+ AND advanced security monitoring and combat support are available

#### Scenario: Blacksmith benefits scale with Forge level
+ GIVEN a Blacksmith NPC is assigned to a location
+ AND the location has Forge level 1-2 and comfort level 3-5
+ WHEN Blacksmith benefits are active
+ THEN forging is enhanced with 10% faster smelting
+ AND basic equipment repair services are available
+ AND basic metalworking recipes are accessible

#### Scenario: Blacksmith benefits scale to high Forge level
+ GIVEN a Blacksmith NPC is assigned to a location
+ AND the location has Forge level 5+ and comfort level 9+
+ WHEN Blacksmith benefits are active
+ THEN forging is enhanced with 30% faster smelting
+ AND significant quality bonus is applied to forged items
+ AND advanced equipment repair services are available
+ AND advanced metalworking recipes are accessible

#### Scenario: Shipwright benefits scale with comfort level
+ GIVEN a Shipwright NPC is assigned to a coastal location
+ AND the location has comfort level 3-5
+ WHEN Shipwright benefits are active
+ THEN sailing speed is improved by 10%
+ AND basic wind handling improvements are applied
+ AND basic ship repair services are available

#### Scenario: Shipwright benefits scale to high comfort
+ GIVEN a Shipwright NPC is assigned to a coastal location
+ AND the location has comfort level 9+
+ WHEN Shipwright benefits are active
+ THEN sailing speed is improved by 30%
+ AND excellent wind handling is applied (can tack into wind at wider angles, sail effectively against headwinds)
+ AND advanced ship repair services are available
+ AND access to advanced ship modifications is provided

#### Scenario: Benefit tier updates when conditions change
+ GIVEN an NPC is providing benefits at a specific tier
+ AND comfort level or workbench level changes
+ WHEN benefit scaling is recalculated
+ THEN benefit tier is updated to match new conditions
+ AND benefits adjust to the new tier level
+ AND the player is notified of benefit tier changes

### Requirement: Tavern Keeper villager-dependent scaling
+ The system SHALL allow Tavern Keeper level to scale directly with Farmer level(s) in the same village or connected settlements.
+ The system SHALL require all three workbenches (Food preparation table, Cauldron, Iron roasting spit) for Tavern Keeper residency.
+ The system SHALL provide different Tavern Keeper benefits based on their level (cooking, brewing, feast comfort bonus).
+ The system SHALL allow Tavern Keeper to be assigned to monitor feasts for additional comfort bonus.
+ The system SHALL require Tavern Keeper cooking and brewing to follow normal cauldron/food preparation table level requirements.

#### Scenario: Tavern Keeper level based on Farmer
+ GIVEN a Tavern Keeper is assigned to a village
+ AND all required workbenches are present (Food preparation table, Cauldron, Iron roasting spit)
+ AND a Farmer (Level 1) is in the same village or connected via trade route
+ WHEN Tavern Keeper level is calculated
+ THEN Tavern Keeper level is set to 1 (based on Farmer level)
+ AND Tavern Keeper can cook basic food items
+ AND cooking follows normal cauldron/food preparation table level requirements
+ AND cooking benefits are available

#### Scenario: Tavern Keeper level increases with Farmer beekeeping
+ GIVEN a Tavern Keeper is assigned to a village
+ AND all required workbenches are present (Food preparation table, Cauldron, Iron roasting spit)
+ AND a Farmer (Level 2+ with beekeeping) is in the same village or connected via trade route
+ WHEN Tavern Keeper level is calculated
+ THEN Tavern Keeper level is set to 2 (based on Farmer level)
+ AND Tavern Keeper can cook food items (follows normal cauldron/food preparation table level requirements)
+ AND Tavern Keeper can brew mead (follows normal cauldron level requirements)
+ AND both cooking and brewing benefits are available

#### Scenario: Tavern Keeper level increases with multiple farmers
+ GIVEN a Tavern Keeper is assigned to a village
+ AND all required workbenches are present (Food preparation table, Cauldron, Iron roasting spit)
+ AND multiple Farmers (Level 2+ with beekeeping) are in the same village or connected via trade route
+ WHEN Tavern Keeper level is calculated
+ THEN Tavern Keeper level is set to 3 (based on multiple/high-level farmers)
+ AND Tavern Keeper can cook food items (follows normal cauldron/food preparation table level requirements)
+ AND Tavern Keeper can brew mead (follows normal cauldron level requirements)
+ AND Tavern Keeper can provide additional comfort bonus when monitoring feasts

#### Scenario: Tavern Keeper monitors feast for comfort bonus
+ GIVEN a Tavern Keeper (Level 3) is assigned to a village
+ AND the Tavern Keeper is assigned to monitor a feast
+ WHEN players or NPCs use the feast
+ THEN additional comfort bonus is provided
+ AND the comfort bonus is greater than a feast without Tavern Keeper monitoring
+ AND the bonus scales with Tavern Keeper level

#### Scenario: Tavern Keeper level updates when Farmer level changes
+ GIVEN a Tavern Keeper is assigned to a village
+ AND the Tavern Keeper level is based on Farmer level
+ AND the Farmer level increases (e.g., reaches Level 2+ for beekeeping)
+ WHEN Tavern Keeper level is recalculated
+ THEN Tavern Keeper level is updated to match new Farmer level
+ AND Tavern Keeper benefits are updated (e.g., brewing becomes available)
+ AND the player is notified of Tavern Keeper level increase

### Requirement: Trade route requirement offsets
+ The system SHALL allow Traders to establish trade routes between settlements connected by roads.
+ The system SHALL provide requirement offsets to NPCs at settlements connected via trade routes.
+ The system SHALL allow NPCs to operate at higher benefit tiers with reduced comfort/workbench requirements when connected to trade routes.

#### Scenario: Trader establishes trade route
+ GIVEN a Trader NPC is assigned to a settlement
+ AND a road connects this settlement to another settlement with a Trader
+ WHEN trade route is established
+ THEN both settlements are connected via trade route
+ AND NPCs at both settlements can benefit from trade route offsets
+ AND the trade route is maintained as long as the road connection exists

#### Scenario: Guard in Ashlands benefits from trade route
+ GIVEN a Guard NPC is assigned to a settlement in Ashlands biome
+ AND the settlement has comfort level 4-6 (below the 10+ normally required for Ashlands-level spawn prevention)
+ AND the settlement is connected via trade route to another settlement with a Trader
+ WHEN Guard benefits are calculated
+ THEN trade route offset is applied
+ AND Guard can operate at higher tier (prevent Ashlands-level enemy spawns)
+ AND Guard provides large patrol area despite lower comfort level
+ AND Guard benefits are equivalent to comfort level 10+ without trade route

#### Scenario: Trade route offsets multiple NPCs
+ GIVEN a settlement is connected via trade route
+ AND multiple NPCs are assigned to the settlement
+ WHEN NPC benefits are calculated
+ THEN all NPCs at the settlement benefit from trade route offsets
+ AND each NPC can operate at higher tier with reduced requirements
+ AND trade route makes building roads between villages meaningful

#### Scenario: Trade route broken removes offsets
+ GIVEN NPCs are benefiting from trade route offsets
+ AND the road connection is broken or destroyed
+ WHEN trade route status is checked
+ THEN trade route is no longer active
+ AND requirement offsets are removed
+ AND NPCs revert to their normal requirement-based benefit tiers
+ AND the player is notified that trade route is broken

### Requirement: Villager interdependence system
+ The system SHALL allow villagers to add levels to other villagers based on their profession.
+ The system SHALL scale villager production capabilities based on the variety of villagers in the same village or connected settlements.
+ The system SHALL enable villager production chains where villagers depend on each other for higher-tier outputs.

#### Scenario: Farmer provides base level to Miner
+ GIVEN a Farmer is assigned to a village
+ AND a Miner is assigned to the same village (or connected via trade route)
+ WHEN villager levels are calculated
+ THEN Farmer provides base level to Miner
+ AND Miner can produce copper ore daily
+ AND Miner production is enabled by Farmer support

#### Scenario: Farmer produces food items
+ GIVEN a Farmer is assigned to a village
+ AND the Farmer has base level (Level 1)
+ WHEN Farmer production is calculated
+ THEN Farmer produces food items (carrots, turnips, etc.) daily
+ AND food items are added to village storage or player inventory

#### Scenario: Farmer produces honey through beekeeping
+ GIVEN a Farmer is assigned to a village
+ AND the Farmer has reached Level 2+
+ AND beehives are present nearby
+ WHEN Farmer production is calculated
+ THEN Farmer produces honey through beekeeping
+ AND honey production scales with comfort level and villager variety
+ AND honey is added to village storage or player inventory

#### Scenario: Blacksmith adds level to Miner
+ GIVEN a Farmer and Miner are in a village
+ AND Miner is producing copper ore (with Farmer support)
+ AND a Blacksmith is assigned to the same village
+ WHEN villager levels are calculated
+ THEN Blacksmith adds an additional level to Miner
+ AND Miner can now produce both copper and tin ore
+ AND Miner production capabilities are enhanced

#### Scenario: Blacksmith produces bronze with Miner support
+ GIVEN a Farmer, Miner, and Blacksmith are in a village
+ AND Miner is producing copper and tin ore (with Farmer + Blacksmith support)
+ WHEN Blacksmith production is calculated
+ THEN Blacksmith can produce bronze daily
+ AND Blacksmith production requires Miner producing both copper and tin
+ AND production chain is complete (Farmer → Miner → Blacksmith)

#### Scenario: Carpenter adds levels to all villagers
+ GIVEN multiple villagers (Farmer, Miner, Blacksmith) are in a village
+ AND a Carpenter is assigned to the same village
+ WHEN villager levels are calculated
+ THEN Carpenter adds levels to Farmer (better farming tools)
+ AND Carpenter adds levels to Miner (better mining tools)
+ AND Carpenter adds levels to Blacksmith (better forging tools)
+ AND all villagers have improved production efficiency

#### Scenario: Villager variety increases production
+ GIVEN a village has multiple villagers (Farmer, Miner, Blacksmith, Carpenter)
+ WHEN villager production is calculated
+ THEN production scales with villager variety
+ AND each villager benefits from the presence of others
+ AND total village output is greater than sum of individual outputs
+ AND interdependence creates bustling, industrious towns

#### Scenario: Trade routes enable distributed production
+ GIVEN a Farmer is in Village A
+ AND a Miner is in Village B
+ AND both villages are connected via trade route
+ WHEN villager levels are calculated
+ THEN Farmer in Village A can provide base level to Miner in Village B
+ AND Miner can produce copper ore despite being in different village
+ AND trade routes enable distributed production networks
