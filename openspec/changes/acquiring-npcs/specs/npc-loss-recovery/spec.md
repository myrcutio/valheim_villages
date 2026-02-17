# npc-loss-recovery - Spec Delta

## ADDED Requirements

### Requirement: NPC death detection and animation
+ The system SHALL detect when an NPC's health reaches zero.
+ The system SHALL determine death type (Valkyrie Take, Hel's Claim, or Fuling Shaman Theft) based on NPC type and death cause.
+ The system SHALL play appropriate death animation based on death type.
+ The system SHALL spawn a grave marker at the NPC's death location.
+ The system SHALL track NPC death state (alive, dead, recovery quest active, revived).

#### Scenario: NPC dies in combat (Valkyrie Take)
+ GIVEN an NPC's health reaches zero
+ AND the death type is determined to be Valkyrie Take (random, more likely if death occurred in combat)
+ WHEN the NPC dies
+ THEN a valkyrie appears and flies away with the NPC
+ AND a grave marker is spawned at the death location
+ AND the NPC's assigned work/benefits immediately stop
+ AND the NPC's state is set to "dead"

#### Scenario: NPC dies from environmental hazard (Hel's Claim)
+ GIVEN an NPC's health reaches zero
+ AND the death type is determined to be Hel's Claim (random, more likely if death occurred via falling tree, drowning, or fire)
+ WHEN the NPC dies
+ THEN the NPC is dragged underground by spectral hands
+ AND a grave marker is spawned at the death location
+ AND the NPC's assigned work/benefits immediately stop
+ AND the NPC's state is set to "dead"

#### Scenario: Trader or Tavern Keeper dies (Fuling Shaman Theft)
+ GIVEN a Trader or Tavern Keeper NPC's health reaches zero
+ WHEN the NPC dies
+ THEN a Fuling Shaman appears and steals the NPC's soul (special animation)
+ AND a grave marker is spawned at the death location
+ AND the NPC's assigned work/benefits immediately stop
+ AND the NPC's state is set to "dead"
+ AND death type is always Fuling Shaman Theft (overrides normal death type determination)

### Requirement: Recovery quest generation
+ The system SHALL generate recovery quests based on NPC death type.
+ The system SHALL place quest markers at appropriate locations for each quest type.
+ The system SHALL track recovery quest state (not started, in progress, completed).
+ The system SHALL scale quest difficulty based on NPC type and level.

#### Scenario: Valkyrie Take recovery quest generated
+ GIVEN an NPC has died with Valkyrie Take death type
+ WHEN the death is detected
+ THEN a quest marker appears in the Mountain biome
+ AND the quest marker indicates a summoning location (stone circle, peak, etc.)
+ AND the recovery quest state is set to "not started"

#### Scenario: Hel's Claim recovery quest generated
+ GIVEN an NPC has died with Hel's Claim death type
+ WHEN the death is detected
+ THEN a quest marker appears at a nearby crypt/tomb (dungeon structure)
+ AND the crypt/tomb is within reasonable distance of the death location
+ AND the recovery quest state is set to "not started"

#### Scenario: Fuling Shaman Theft recovery quest generated
+ GIVEN a Trader or Tavern Keeper has died
+ WHEN the death is detected
+ THEN a quest marker appears in Plains or Ashlands biome
+ AND the quest marker indicates the location of the Fuling Shaman who stole the soul
+ AND the recovery quest state is set to "not started"

### Requirement: Valkyrie Take recovery quest mechanics
+ The system SHALL allow players to initiate Valkyrie Take recovery quests at summoning locations.
+ The system SHALL require players to sacrifice honey and roast meat to summon the boss.
+ The system SHALL spawn a level-appropriate fallen valkyrie boss when summoned.
+ The system SHALL drop a Hrungnir Heart upon boss defeat.

#### Scenario: Player initiates Valkyrie Take recovery quest
+ GIVEN a Valkyrie Take recovery quest exists
+ AND the player has traveled to the summoning location
+ WHEN the player interacts with the summoning location
+ THEN the system checks if the player has honey and roast meat
+ AND if the player has the required items, they can sacrifice them to summon the boss
+ AND the recovery quest state changes to "in progress"

#### Scenario: Player defeats fallen valkyrie boss
+ GIVEN a Valkyrie Take recovery quest is in progress
+ AND the player has summoned the fallen valkyrie boss
+ WHEN the player defeats the boss
+ THEN a Hrungnir Heart drops at the summoning location
+ AND the heart tier is determined by NPC type (see Hrungnir Heart System)
+ AND the recovery quest state changes to "completed"

### Requirement: Hel's Claim recovery quest mechanics
+ The system SHALL spawn the NPC's corrupted form (ghost or draugr) in the crypt/tomb.
+ The system SHALL determine corruption form based on NPC level (ghost for low-level, draugr for higher-level).
+ The system SHALL require players to defeat the corrupted NPC form.
+ The system SHALL drop a Hrungnir Heart when the area is safe after defeating the corrupted form.

#### Scenario: Player enters crypt with corrupted NPC
+ GIVEN a Hel's Claim recovery quest exists
+ AND the player enters the crypt/tomb
+ WHEN the player approaches the quest location
+ THEN the NPC's corrupted form spawns (ghost for low-level NPCs, draugr for higher-level NPCs)
+ AND the corrupted form is hostile to the player
+ AND the recovery quest state changes to "in progress"

#### Scenario: Player defeats corrupted NPC form
+ GIVEN a Hel's Claim recovery quest is in progress
+ AND the player has engaged the corrupted NPC form
+ WHEN the player defeats the corrupted NPC form
+ AND the area is safe (no nearby enemies)
+ THEN a Hrungnir Heart drops at the crypt location
+ AND the heart tier is determined by NPC type and level (see Hrungnir Heart System)
+ AND the recovery quest state changes to "completed"

### Requirement: Fuling Shaman Theft recovery quest mechanics
+ The system SHALL spawn a Fuling Shaman and their warband at the quest location.
+ The system SHALL require players to defeat the shaman and warband.
+ The system SHALL drop a Ruby Hrungnir Heart upon shaman defeat.

#### Scenario: Player approaches Fuling Shaman location
+ GIVEN a Fuling Shaman Theft recovery quest exists
+ AND the player travels to the quest location
+ WHEN the player approaches the location
+ THEN a Fuling Shaman and their warband spawn
+ AND the recovery quest state changes to "in progress"

#### Scenario: Player defeats Fuling Shaman
+ GIVEN a Fuling Shaman Theft recovery quest is in progress
+ AND the player has engaged the Fuling Shaman and warband
+ WHEN the player defeats the Fuling Shaman
+ THEN a Ruby Hrungnir Heart drops (highest tier, 900kg)
+ AND the recovery quest state changes to "completed"

### Requirement: Hrungnir Heart system
+ The system SHALL provide three tiers of Hrungnir Hearts with different weights.
+ The system SHALL determine heart tier based on NPC type and quest location.
+ The system SHALL prevent hearts from being carried through normal portals (like ore/metal).
+ The system SHALL allow hearts to be transported through stone portals (Ruby Heart only).
+ The system SHALL make hearts visually distinct (glow or pulse to indicate NPC soul inside).

#### Scenario: Shard of Hrungnir Heart drops for villager NPC
+ GIVEN a recovery quest is completed
+ AND the NPC type is a villager (Farmer, Miner, Blacksmith, Carpenter)
+ AND the quest location is in Meadows or Black Forest
+ WHEN the heart drops
+ THEN a Shard of Hrungnir Heart drops (20kg weight)
+ AND the heart glows or pulses to indicate NPC soul inside

#### Scenario: Hrungnir Heart drops for low-level specialist NPC
+ GIVEN a recovery quest is completed
+ AND the NPC type is a low-level specialist (Scout, Guard, Mountaineer)
+ AND the quest location is in Swamp, Mountains, or Mistlands
+ WHEN the heart drops
+ THEN a Hrungnir Heart drops (200kg weight, same as Dragon Egg)
+ AND the heart glows or pulses to indicate NPC soul inside

#### Scenario: Ruby Hrungnir Heart drops for Trader or Tavern Keeper
+ GIVEN a recovery quest is completed
+ AND the NPC type is Trader or Tavern Keeper
+ AND the quest location is in Plains or Ashlands
+ WHEN the heart drops
+ THEN a Ruby Hrungnir Heart drops (900kg weight)
+ AND the heart glows or pulses to indicate NPC soul inside

#### Scenario: Heart cannot be carried through normal portal
+ GIVEN a player is carrying a Hrungnir Heart
+ AND the player approaches a normal portal
+ WHEN the player attempts to use the portal
+ THEN the portal does not activate
+ AND the player receives a notification that the heart cannot be carried through portals

#### Scenario: Ruby Heart can be carried through stone portal
+ GIVEN a player is carrying a Ruby Hrungnir Heart
+ AND the player approaches a stone portal
+ WHEN the player attempts to use the stone portal
+ THEN the portal activates normally
+ AND the player and heart are transported through the portal

### Requirement: Heart transport mechanics
+ The system SHALL allow Shard Hearts (20kg) to be carried in player inventory.
+ The system SHALL allow Hrungnir Hearts (200kg) to be transported in carts or ships.
+ The system SHALL allow Ruby Hearts (900kg) to be carried with maximum carry setup (Megingjord + Moder's power + Mead of Troll Strength).
+ The system SHALL allow Ruby Hearts (900kg) to be transported in carts, ships, or stone portals.

#### Scenario: Player carries Shard Heart in inventory
+ GIVEN a player has obtained a Shard of Hrungnir Heart (20kg)
+ WHEN the player picks up the heart
+ THEN the heart is added to player inventory
+ AND the heart adds 20kg to player encumbrance
+ AND the player can move normally (moderate encumbrance)

#### Scenario: Player transports Hrungnir Heart in cart
+ GIVEN a player has obtained a Hrungnir Heart (200kg)
+ WHEN the player places the heart in a cart
+ THEN the cart can carry the heart
+ AND the player can transport the heart overland using the cart

#### Scenario: Player transports Hrungnir Heart on ship
+ GIVEN a player has obtained a Hrungnir Heart (200kg)
+ WHEN the player places the heart on a ship
+ THEN the ship can carry the heart (like Dragon Eggs)
+ AND the player can transport the heart by water

#### Scenario: Player carries Ruby Heart with maximum carry setup
+ GIVEN a player has obtained a Ruby Hrungnir Heart (900kg)
+ AND the player is wearing Megingjord belt
+ AND the player has Moder's power active
+ AND the player has consumed Mead of Troll Strength
+ WHEN the player picks up the heart
+ THEN the player can carry the heart (though movement is very slow)
+ AND the carry capacity is time-sensitive (Moder's power and Mead wear off)

#### Scenario: Player transports Ruby Heart in cart
+ GIVEN a player has obtained a Ruby Hrungnir Heart (900kg)
+ WHEN the player places the heart in a cart
+ THEN the cart can carry the heart
+ AND the player can transport the heart overland using the cart

### Requirement: NPC revival mechanics
+ The system SHALL require players to return the Hrungnir Heart to the NPC's grave marker at the death location.
+ The system SHALL allow players to use the heart on the grave marker to revive the NPC.
+ The system SHALL revive the NPC at the exact death location.
+ The system SHALL preserve NPC stats, equipment, and relationships upon revival.
+ The system SHALL consume the heart during the revival process.

#### Scenario: Player uses heart on grave marker
+ GIVEN a player has obtained a Hrungnir Heart
+ AND the player has traveled to the NPC's death location
+ AND a grave marker exists at the location
+ WHEN the player uses the heart on the grave marker
+ THEN the NPC is revived at the exact death location
+ AND the NPC retains all stats, equipment, and relationships
+ AND the heart is consumed
+ AND the NPC's state changes to "revived"

#### Scenario: Heart is not associated with specific NPC
+ GIVEN a Hrungnir Heart exists
+ WHEN the system checks heart association
+ THEN the heart is not directly associated with any specific NPC
+ AND only the grave marker is associated with the NPC
+ AND any heart can be used on any grave marker (if appropriate tier)

### Requirement: Revived NPC dialogue and effects
+ The system SHALL provide revived NPCs with new dialogue option about their underworld journey.
+ The system SHALL display a unique blue aura on revived NPCs at night.
+ The system SHALL grant all NPCs in the village a mood bonus "Legendary story" when underworld dialogue is used (non-stackable).
+ The system SHALL add 1 comfort to the village when underworld dialogue is used.

#### Scenario: Revived NPC gains new dialogue option
+ GIVEN an NPC has been revived from death
+ WHEN the player interacts with the NPC
+ THEN a new dialogue option appears: "Tell me about your journey through the underworld"
+ AND the NPC can share their story of their experience in Hel/Valhalla

#### Scenario: Revived NPC displays blue aura at night
+ GIVEN an NPC has been revived from death
+ AND it is nighttime
+ WHEN the NPC is visible
+ THEN the NPC displays a unique blue aura
+ AND the aura is visible to the player

#### Scenario: Player uses underworld dialogue option
+ GIVEN a revived NPC has the underworld dialogue option available
+ WHEN the player selects "Tell me about your journey through the underworld"
+ THEN the NPC shares their story
+ AND all NPCs in the village gain a mood bonus "Legendary story" (non-stackable)
+ AND the village gains 1 comfort
+ AND the dialogue option may become unavailable or change after use

### Requirement: Quest state tracking
+ The system SHALL track recovery quest state for each dead NPC.
+ The system SHALL allow multiple recovery quests to be active simultaneously.
+ The system SHALL provide UI/notifications for NPC loss and recovery status.

#### Scenario: Multiple NPCs die
+ GIVEN multiple NPCs have died
+ WHEN the system tracks their states
+ THEN each NPC has an independent recovery quest state
+ AND multiple recovery quests can be active simultaneously
+ AND the player can see all active recovery quests

#### Scenario: Player receives notification of NPC death
+ GIVEN an NPC has died
+ WHEN the death is detected
+ THEN the player receives a notification indicating the NPC's death
+ AND the notification indicates the death type (Valkyrie Take, Hel's Claim, or Fuling Shaman Theft)
+ AND a quest marker appears on the map

### Requirement: Integration with existing systems
+ The system SHALL integrate with existing NPC assignment systems.
+ The system SHALL preserve NPC type and specialization after revival.
+ The system SHALL integrate with existing rescue quest infrastructure where applicable.

#### Scenario: Revived NPC retains type and specialization
+ GIVEN an NPC has been revived from death
+ WHEN the NPC is revived
+ THEN the NPC retains their original type (Farmer, Miner, Blacksmith, etc.)
+ AND the NPC retains their specialization and level
+ AND the NPC can be reassigned to villages or constructions

#### Scenario: Revived NPC integrates with assignment system
+ GIVEN a revived NPC is available
+ WHEN the player attempts to assign the NPC to a village or construction
+ THEN the system uses existing villager assignment logic
+ AND residency requirements are checked (if applicable)
+ AND the NPC is assigned following standard assignment procedures
