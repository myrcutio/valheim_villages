# Tasks: acquiring-npcs

## Implementation Checklist

*Note: Tasks focus on modifying existing code rather than adding new systems. Tasks will be refined as code locations are identified during implementation.*

### Phase 1: Discovery and Design
- [ ] Identify existing world generation/placement code to modify for NPC locations
- [ ] Identify existing exploration/discovery systems to extend for NPC discovery
- [ ] Identify existing map/marker systems to display NPC locations
- [ ] Identify existing quest/objective systems to track rescue quest state
- [ ] Design captive NPC location placement system
- [ ] Design discovery mechanism data structures (map fragments with traits, clues, etc.)
- [ ] Design fragment trait system (biome, profession hint, location type)
- [ ] Design fragment combination logic (matching traits to determine NPC type)
- [ ] Design rescue encounter system architecture

### Phase 2: NPC Location Placement
- [ ] Modify world generation systems to place captive NPC locations
- [ ] Implement NPC location placement logic (biome-appropriate, distributed)
- [ ] Create NPC location data structures (position, NPC type, captors, difficulty)
- [ ] Add NPC location validation (ensure valid placement, no overlaps)
- [ ] Integrate with existing structure/entity placement systems

### Phase 3: Discovery Mechanisms
- [ ] Modify item/loot systems to add map fragments with traits as discoverable items
- [ ] Implement fragment trait system (biome, profession hint, location type)
- [ ] Implement map fragment collection and tracking
- [ ] Implement fragment combination logic (matching traits to determine NPC type)
- [ ] Add fragment sources for exploration (unveiling map areas, landmarks, objectives)
- [ ] Integrate fragment discovery with map exploration systems
- [ ] Add biome-specific fragment distribution
- [ ] Add elevation and coastal exploration fragment sources
- [ ] Implement villager story system (mead conversations)
- [ ] Add mead requirement checking for story types
- [ ] Implement story type selection (player requests specific NPC type stories)
- [ ] Add story reward system (NPC location information or targeted fragments)
- [ ] Integrate with mead/brewing systems (Tavern Keeper, Farmer beekeeping)
- [ ] Modify exploration systems to add environmental clue detection
- [ ] Implement environmental clue system (sounds, visual indicators, abandoned items)
- [ ] Modify exploration reward systems to reveal NPC locations when discovering new areas
- [ ] Add discovery state tracking (undiscovered, discovered, rescued)

### Phase 4: Map Display and Markers
- [ ] Modify map/marker systems to display discovered NPC locations
- [ ] Implement map marker creation for discovered NPC locations
- [ ] Add marker differentiation (discovered vs. rescued, NPC type indicators)
- [ ] Integrate with existing map systems

### Phase 5: Rescue Quest Mechanics
- [ ] Modify NPC spawning systems to spawn captive NPCs at designated locations
- [ ] Implement captor entity spawning (draugr, goblins, trolls, etc.)
- [ ] Modify combat/encounter systems to create rescue encounters
- [ ] Implement rescue quest state tracking (discovered, in progress, completed)
- [ ] Add rescue completion logic (defeat captors, free NPC)
- [ ] Implement rescue failure handling (NPC remains captive, can retry)

### Phase 6: NPC Integration
- [ ] Modify NPC assignment systems to handle rescued NPCs
- [ ] Implement rescued NPC availability for village assignment
- [ ] Integrate rescued NPCs with villager residency requirements
- [ ] Connect rescued NPCs to construction inhabitation system
- [ ] Add NPC type specialization (all types from villager-npc-types: Farmer, Miner, Blacksmith, Carpenter, Scout, Trader, Guard, Mountaineer, Shipwright, Tavern Keeper)

### Phase 7: Testing and Validation
- [ ] Test NPC location placement across different biomes
- [ ] Test discovery mechanisms (map fragments, environmental clues, exploration rewards)
- [ ] Test rescue encounters at various difficulty levels
- [ ] Test NPC assignment after rescue
- [ ] Validate end-to-end flow: discovery → rescue → assignment

### Phase 8: Specification
- [ ] Create npc-rescue-quests spec with requirements and scenarios
- [ ] Create npc-location-discovery spec with requirements and scenarios
- [ ] Document integration points with other systems
- [ ] Add scenarios for each discovery mechanism
- [ ] Add scenarios for rescue encounters

## Notes

- Tasks will be refined as specific code locations are identified during implementation
- All tasks should modify existing code rather than creating new parallel systems
- Discovery mechanisms can be implemented incrementally (start with map fragments, add others)
- NPC types and their benefits are defined in villager-npc-types proposal
- All NPC types (Villagers and Specialists) can be discovered and rescued through this system
- Integration with villager-residency-requirements and make-villages-useful changes should be coordinated
