# npc-location-discovery - Spec Delta

## REPLACED Requirements

*Note: This spec replaces the previous trait-based map fragment system with a simpler biome-colored ransom note fragment system.*

## ADDED Requirements

### Requirement: Ransom note fragment items
+ The system SHALL provide biome-specific ransom note fragment items, one for each major biome.
+ The system SHALL color-code fragments by biome (Meadows green, Black Forest dark blue, Swamp brown, Mountain white, Plains gold, Mistlands purple, Ashlands crimson).
+ The system SHALL make fragments stackable (up to 10 per stack) and lightweight.

#### Scenario: Fragment item exists for each biome
+ GIVEN the game world has biomes (Meadows, Black Forest, Swamp, Mountain, Plains, Mistlands, Ashlands)
+ WHEN fragment items are registered
+ THEN each biome has a corresponding ransom note fragment item
+ AND each fragment has a distinct biome-themed name and description
+ AND each fragment indicates its biome affiliation

### Requirement: Fragment discovery in chests and near runestones
+ The system SHALL add biome-appropriate fragments to chest loot tables.
+ The system SHALL spawn fragments near runestones with a random chance.
+ The system SHALL match fragment biome to the biome where the chest or runestone is located.

#### Scenario: Fragment found in chest
+ GIVEN a chest exists in a biome (e.g., Black Forest)
+ AND the chest has a loot table with existing items
+ WHEN the chest generates its contents
+ THEN a biome-matching fragment (e.g., Black Forest Ransom Fragment) may be included
+ AND the fragment has a low drop weight relative to other loot

#### Scenario: Fragment found near runestone
+ GIVEN a runestone exists in the world
+ AND the runestone has not previously spawned a fragment
+ WHEN the runestone is first loaded by a player
+ THEN there is a 30% chance a biome-matching fragment spawns nearby
+ AND the fragment appears within a small radius of the runestone
+ AND the spawn state is persisted (no duplicate spawns)

#### Scenario: Fragments match local biome
+ GIVEN a container or runestone is located in a specific biome
+ WHEN a fragment is generated or spawned
+ THEN the fragment matches the biome of that location
+ AND Meadows locations produce Meadows fragments
+ AND Black Forest locations produce Black Forest fragments
+ AND so on for each biome

### Requirement: Fragment combination into rescue quest
+ The system SHALL allow players to combine 3 fragments of the same biome to create a rescue quest.
+ The system SHALL consume the 3 fragments when combined.
+ The system SHALL place a rescue quest map marker on the combining player's map.
+ The system SHALL determine the NPC type of the rescue quest based on the fragment biome.

#### Scenario: Player combines 3 same-biome fragments
+ GIVEN a player has 3 or more fragments of the same biome in their inventory
+ WHEN the player uses (right-clicks) any fragment of that biome
+ THEN 3 fragments of that biome are consumed from the inventory
+ AND a rescue quest map marker is placed on the player's map
+ AND the marker indicates the NPC type and biome
+ AND the player receives a message about the revealed quest

#### Scenario: Player has insufficient fragments
+ GIVEN a player has fewer than 3 fragments of a specific biome
+ WHEN the player uses a fragment of that biome
+ THEN no fragments are consumed
+ AND the player is informed how many more fragments are needed
+ AND the interaction is consumed (no other action taken)

#### Scenario: Biome determines rescue NPC type
+ GIVEN a player combines 3 fragments of a specific biome
+ WHEN the rescue quest is created
+ THEN Meadows fragments reveal a captive Farmer
+ AND Black Forest fragments reveal a captive Miner
+ AND Swamp fragments reveal a captive Miner
+ AND Mountain fragments reveal a captive Mountaineer
+ AND Plains fragments reveal a captive Farmer
+ AND Mistlands fragments reveal a captive Scout
+ AND Ashlands fragments reveal a captive Guard

### Requirement: Quest marker placement
+ The system SHALL place rescue quest markers at valid locations within the fragment's biome.
+ The system SHALL search for biome-matching terrain near the player's location.
+ The system SHALL use Valheim's map pin system to display markers.

#### Scenario: Quest marker placed in matching biome
+ GIVEN a player combines 3 fragments
+ WHEN the system searches for a quest location
+ THEN the system samples random positions within a search radius of the player
+ AND the system finds a position that matches the fragment's biome
+ AND a map pin is placed at that position
+ AND the pin label indicates the NPC type and biome (e.g., "Rescue: Captive Mountaineer (Mountain)")

#### Scenario: Fallback when biome not found nearby
+ GIVEN a player combines 3 fragments
+ AND no matching biome terrain is found within the search radius
+ WHEN the system cannot find a valid biome position
+ THEN a map pin is placed at a random offset from the player
+ AND the player is notified that the captive may be found in the target biome

### Requirement: Map marker display
+ The system SHALL display rescue quest locations on the player's map.
+ The system SHALL use Valheim's native map pin system for markers.
+ The system SHALL label pins with NPC type and biome information.

#### Scenario: Rescue quest appears on map
+ GIVEN a player has combined fragments to create a rescue quest
+ WHEN the player opens the map
+ THEN the rescue quest location is visible as a map pin
+ AND the pin shows the NPC type and biome in its label
+ AND the pin persists across game sessions
