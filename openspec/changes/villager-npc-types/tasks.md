# Tasks: villager-npc-types

## Implementation Checklist

*Note: Tasks focus on modifying existing code rather than adding new systems. Tasks will be refined as code locations are identified during implementation.*

### Phase 1: Discovery and Design
- [x] Identify existing NPC/villager data structures to modify
- [x] Identify existing workbench systems and level detection
- [x] Identify existing biome detection systems
- [x] Identify existing comfort calculation systems
- [x] Identify existing construction/material detection systems
- [x] Design NPC type data structure (type, requirements, benefits)
- [x] Design requirement validation system architecture
- [x] Design benefit integration system architecture
- [x] Design tiered benefit scaling system (comfort-based, workbench-based, combined)

### Phase 2: NPC Type Definitions
- [x] Create NPC type data structure with type identifier
- [x] Implement requirement structure (workbenches, biome, comfort, materials)
- [x] Implement benefit structure (benefit type, effect, conditions)
- [x] Define Farmer NPC type (Villager) with requirements and production
- [x] Define Miner NPC type (Villager) with requirements and production
- [x] Define Blacksmith NPC type (Villager) with requirements and production
- [x] Define Carpenter NPC type (Villager) with requirements and production
- [x] Define Scout NPC type (Specialist) with requirements and benefits
- [x] Define Trader NPC type (Specialist) with requirements and benefits
- [x] Define Guard NPC type (Specialist) with requirements and benefits
- [x] Define Mountaineer NPC type (Specialist) with requirements and benefits
- [x] Define Shipwright NPC type (Specialist) with requirements and benefits
- [x] Define Tavern Keeper NPC type (Specialist) with requirements and villager-dependent scaling

### Phase 3: Requirement Validation - Workbenches
- [ ] Modify workbench detection systems to expose workbench type and level
- [ ] Implement workbench requirement checking logic
- [ ] Add validation for workbench level requirements
- [ ] Integrate workbench validation into NPC type requirement system

### Phase 4: Requirement Validation - Biome
- [ ] Modify biome detection systems to expose current biome
- [ ] Implement biome requirement checking logic
- [ ] Add support for biome preferences (required vs. preferred)
- [ ] Integrate biome validation into NPC type requirement system

### Phase 5: Requirement Validation - Comfort
- [ ] Modify comfort calculation systems to expose comfort level
- [ ] Implement comfort requirement checking logic
- [ ] Add validation for minimum comfort level requirements
- [ ] Integrate comfort validation into NPC type requirement system

### Phase 6: Requirement Validation - Materials
- [ ] Modify construction/material systems to detect building materials
- [ ] Implement material requirement checking logic
- [ ] Add validation for required construction materials and structures
- [ ] Integrate material validation into NPC type requirement system

### Phase 7: Requirement Validation Integration
- [ ] Implement comprehensive requirement validation (all four categories)
- [ ] Add requirement status tracking (met/unmet for each category)
- [ ] Implement requirement validation triggers (assignment, periodic, benefit use)
- [ ] Add player notification for unmet requirements

### Phase 8: Villager Production System
- [ ] Implement villager production system (daily resource output)
- [ ] Implement villager interdependence system (level addition between villagers)
- [ ] Implement production chain logic (Farmer → Miner → Blacksmith)
- [ ] Implement villager variety scaling (production increases with variety)
- [ ] Implement Farmer beekeeping system (Level 2+ production, requires beehives)
- [ ] Add production output to player inventory or storage
- [ ] Integrate trade routes with villager interdependence (distributed production)

### Phase 9: Benefit System Implementation
- [ ] Modify crafting systems to integrate Blacksmith production (for Specialists, if applicable)
- [ ] Modify farming systems to integrate Farmer production
- [ ] Modify exploration systems to integrate Scout benefits
- [ ] Modify map systems to integrate Scout discovery radius and marking features
- [ ] Modify wisplight systems to integrate Scout radius improvements (Mistlands)
- [ ] Add dungeon and spawner marking functionality for Scout
- [ ] Modify trade systems to integrate Trader benefits
- [ ] Implement trade route establishment system (roads between settlements with Traders)
- [ ] Implement trade route requirement offset system
- [ ] Add trade route status tracking and validation
- [ ] Integrate trade route offsets into NPC benefit tier calculation
- [ ] Modify crafting systems to integrate Carpenter production
- [ ] Modify combat systems to integrate Guard benefits
- [ ] Modify movement/terrain systems to integrate Mountaineer benefits
- [ ] Modify sailing/movement systems to integrate Shipwright benefits (sailing speed, wind handling)
- [ ] Modify ship repair systems to integrate Shipwright benefits
- [ ] Implement Tavern Keeper villager-dependent scaling system (level based on Farmer level)
- [ ] Modify cooking/brewing systems to integrate Tavern Keeper benefits
- [ ] Modify comfort/feast systems to integrate Tavern Keeper feast monitoring
- [ ] Implement benefit activation/deactivation based on requirements
- [ ] Implement tiered benefit scaling system
- [ ] Add comfort-based benefit scaling (e.g., Guard patrol area scaling)
- [ ] Add workbench-based benefit scaling (e.g., Blacksmith forging scaling)
- [ ] Add combined scaling for NPCs that use both factors
- [ ] Implement benefit tier calculation and updates

### Phase 10: Testing and Validation
- [ ] Test requirement validation for each NPC type
- [ ] Test benefit activation when requirements are met
- [ ] Test benefit deactivation when requirements are unmet
- [ ] Test tiered benefit scaling at different comfort levels
- [ ] Test tiered benefit scaling at different workbench levels
- [ ] Test benefit tier updates when conditions change
- [ ] Test villager interdependence (Farmer → Miner → Blacksmith chain)
- [ ] Test villager production scaling with variety
- [ ] Test trade route integration with villager interdependence
- [ ] Test requirement validation across different biomes and constructions
- [ ] Validate all NPC types have clear, testable requirements and benefits

### Phase 11: Specification
- [x] Create villager-npc-types spec with requirements and scenarios
- [x] Document all NPC types with their requirements and benefits
- [x] Add scenarios for requirement validation
- [x] Add scenarios for benefit activation
- [x] Add scenarios for tiered benefit scaling
- [x] Add scenarios for villager interdependence system
- [x] Add scenarios for villager production chains
- [x] Document integration points with other systems

## Notes

- Tasks will be refined as specific code locations are identified during implementation
- All tasks should modify existing code rather than creating new parallel systems
- NPC type definitions can be implemented incrementally (start with basic types, add specialized types)
- Requirement validation should integrate with existing game systems (workbenches, biomes, comfort, materials)
- Benefit implementation should integrate with existing gameplay systems (crafting, combat, exploration, etc.)
- Integration with other proposals (acquiring-npcs, make-villages-useful, add-villager-residency-requirements) should be coordinated
