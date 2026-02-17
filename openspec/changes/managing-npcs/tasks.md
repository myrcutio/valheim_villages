# Tasks: managing-npcs

## Implementation Tasks

### Phase 1: NPC Placement and Assignment

1. **Create NPC item type (swaddled villager)**
   - Add NPC item definition to item system
   - Item can be placed in inventory and back slot
   - Item can be assigned to quickslots (1-9)
   - Validation: Item appears in inventory, can be moved to quickslot

2. **Implement quickslot NPC usage**
   - Modify quickslot interaction to handle NPC items
   - Allow using NPC item from quickslot on structures
   - Validation: NPC item can be used from quickslot

3. **Implement NPC assignment to beds**
   - Modify bed interaction to accept NPC items
   - Create NPC assignment logic for beds
   - Store NPC-bed association
   - Validation: NPC can be assigned to bed, association persists

4. **Implement NPC assignment to workbenches**
   - Modify workbench interaction to accept NPC items
   - Create NPC assignment logic for workbenches
   - Store NPC-workbench association
   - Validation: NPC can be assigned to workbench, association persists

5. **Implement NPC assignment state tracking**
   - Create data structure for NPC-location associations
   - Track which NPCs are assigned to which locations
   - Persist assignment state across game sessions
   - Validation: Assignments tracked correctly, persist across sessions

6. **Implement NPC reassignment**
   - Allow reassigning NPCs to different locations
   - Remove old assignment when new assignment is made
   - Validation: NPC can be reassigned, old assignment removed

### Phase 2: NPC Basic Needs System

7. **Implement basic needs tracking**
   - Create data structure for tracking NPC needs (bed, food, fire, safety)
   - Add periodic need checking system
   - Validation: System tracks all four needs for each NPC

8. **Implement bed need validation**
   - Check for nearby beds within reasonable proximity
   - Validate bed access for assigned NPCs
   - Validation: Bed need correctly identified as met/unmet

9. **Implement food need validation**
   - Check for accessible food in nearby storage or food preparation areas
   - Validate food access for assigned NPCs
   - Validation: Food need correctly identified as met/unmet

10. **Implement fire need validation**
    - Check for nearby fire sources (campfire, hearth, etc.)
    - Validate fire access for assigned NPCs
    - Validation: Fire need correctly identified as met/unmet

11. **Implement safety need validation**
    - Check if NPC location is in enclosed structure
    - Validate protection from roaming enemies
    - Validation: Safety need correctly identified as met/unmet

12. **Implement stressed debuff**
    - Create "stressed" debuff definition
    - Apply debuff when any basic need is unmet
    - Remove debuff when all needs are met
    - Validation: Stressed debuff applied/removed correctly

13. **Implement stressed debuff effects**
    - Prevent quota work when stressed
    - Disable NPC benefits when stressed
    - Validation: Stressed NPCs cannot work or provide benefits

### Phase 3: NPC Interaction Menu System

14. **Create NPC interaction menu framework**
    - Create workbench-style menu UI for NPC interaction
    - Implement menu display on NPC interaction
    - Add multiple tabs (options, crafting, upgrade)
    - Validation: Menu displays correctly when interacting with NPC

15. **Implement context-dependent options**
    - Show options based on NPC type
    - Show options based on unlocked features
    - Hide/gray unavailable options
    - Validation: Options display correctly based on context

16. **Implement initial interaction options**
    - Add "Hop on!" option (mount/ride functionality)
    - Add "Have a drink with me!" option (social interaction)
    - Validation: Initial options available and functional

17. **Implement NPC crafting menu**
    - Create crafting tab in NPC interaction menu
    - Add crafting interface following workbench style
    - Validation: Crafting tab displays and functions correctly

18. **Implement swaddled villager crafting**
    - Add "swaddled villager" item to NPC crafting menu
    - Implement crafting recipe and production
    - Validation: Player can craft swaddled villager from NPC menu

19. **Implement map fragment crafting**
    - Add "map fragment" item to NPC crafting menu (for appropriate NPC types)
    - Implement crafting recipe and production
    - Validation: Player can craft map fragment from appropriate NPCs

20. **Implement NPC type-specific crafting items**
    - Add crafting items based on NPC type and capabilities
    - Validation: Different NPC types show different crafting options

### Phase 4: NPC Mood System

21. **Implement mood level tracking**
    - Create data structure for NPC mood (0-100 scale)
    - Track mood level for each NPC
    - Persist mood level across game sessions
    - Validation: Mood level tracked and persisted correctly

22. **Implement permanent mood from location assignments**
    - Calculate mood improvement from assigned locations (bed, feast)
    - Store permanent mood improvements
    - Validation: Location assignments improve mood permanently

23. **Implement temporary mood from bribes**
    - Calculate mood improvement from mead/gold bribes
    - Track temporary mood boosts separately
    - Validation: Bribes provide temporary mood boosts

24. **Implement mood calculation**
    - Combine permanent and temporary mood sources
    - Calculate total mood level
    - Validation: Total mood calculated correctly from all sources

25. **Implement mood decay system**
    - Create decay timers for temporary mood boosts
    - Gradually reduce temporary boosts over time
    - Validation: Temporary mood decays correctly over time

26. **Implement mood threshold checking**
    - Define minimum mood threshold for quota work
    - Check mood threshold before allowing quota seeking
    - Validation: Mood threshold prevents work when not met

### Phase 5: NPC Upgrade System

27. **Implement upgrade tab**
    - Create upgrade tab in NPC interaction menu
    - Display current mood level and requirements
    - Validation: Upgrade tab displays correctly

28. **Implement location assignment in upgrade tab**
    - Allow assigning nearby beds to NPCs
    - Allow assigning nearby feasts to NPCs
    - Display assigned locations
    - Validation: Location assignments work through upgrade tab

29. **Implement bribe system in upgrade tab**
    - Allow providing mead as bribe
    - Allow providing gold as bribe
    - Consume mead/gold from inventory
    - Validation: Bribes work correctly, resources consumed

30. **Implement bribe boost levels**
    - Different mead types provide different boost levels
    - Different gold amounts provide different boost levels
    - Validation: Boost levels scale with bribe type/amount

31. **Implement temporary boost tracking**
    - Track each temporary boost separately
    - Track decay timers for each boost
    - Display temporary boosts in upgrade tab
    - Validation: Temporary boosts tracked and displayed correctly

### Phase 6: Job Board System

32. **Implement Job Board discovery**
    - Detect first NPC pickup/acquisition
    - Unlock Job Board building on discovery
    - Persist discovery state
    - Validation: Job Board discovered on first NPC pickup

33. **Create Job Board building**
    - Define Job Board building type (storage chest-like)
    - Add Job Board to building menu after discovery
    - Implement Job Board construction
    - Validation: Job Board can be built after discovery

34. **Implement Job Board storage**
    - Make Job Board function as storage container
    - Allow placing/removing items from Job Board
    - Validation: Job Board stores items like a chest

35. **Create quota item type**
    - Define quota as scroll item type
    - Add quota item properties (task description, etc.)
    - Validation: Quota items can be created and stored

36. **Implement quota task descriptions**
    - Add task description system to quotas
    - Support cooking, chopping, patrolling, and other task types
    - Validation: Quotas display task descriptions correctly

37. **Implement generalized quota system**
    - Create expandable quota framework
    - Support various task types that players can perform
    - Validation: Quota system supports multiple task types

### Phase 7: NPC Quota Work System

38. **Implement quota seeking behavior**
    - Check mood threshold before seeking
    - Search for quotas in nearby Job Boards
    - Find quotas within reasonable proximity
    - Validation: NPCs seek quotas when mood threshold met

39. **Implement quota selection logic**
    - Select quotas based on NPC type and capabilities
    - Consider proximity in selection
    - Handle multiple available quotas
    - Validation: NPCs select appropriate quotas

40. **Implement quota work execution framework**
    - Create work execution system for quotas
    - Support different task types
    - Track work progress
    - Validation: NPCs can execute quota tasks

41. **Implement cooking quota work**
    - Execute cooking actions for cooking quotas
    - Use food preparation equipment
    - Produce cooked food items
    - Validation: NPCs can work on cooking quotas

42. **Implement chopping lumber quota work**
    - Execute woodcutting actions for lumber quotas
    - Process trees or wood sources
    - Produce lumber or wood items
    - Validation: NPCs can work on chopping quotas

43. **Implement patrolling quota work**
    - Execute movement/patrol actions for patrolling quotas
    - Move along patrol routes
    - Validation: NPCs can work on patrolling quotas

44. **Implement quota work completion**
    - Detect when quota tasks are completed
    - Provide mood bonuses or rewards on completion
    - Validation: Quota completion detected and rewarded

45. **Implement quota work state tracking**
    - Track which NPCs are working on which quotas
    - Prevent multiple NPCs from working same quota (unless allowed)
    - Handle work interruptions
    - Validation: Quota work state tracked correctly

46. **Implement NPC quota work loop**
    - Allow NPCs to seek new quotas after completing one
    - Continue working while mood threshold is met
    - Validation: NPCs continuously work on quotas when conditions met

### Phase 8: Integration and Polish

47. **Integrate with NPC acquisition system**
    - Ensure NPCs from rescue quests can be managed
    - Validate NPC type preservation through management system
    - Validation: Acquired NPCs work with management system

48. **Integrate with NPC type system**
    - Use NPC types to determine available options
    - Use NPC types for quota selection and work execution
    - Validation: NPC types properly integrated

49. **Add UI feedback and notifications**
    - Notify players when NPCs become stressed
    - Display mood level changes
    - Show quota work progress
    - Validation: UI provides clear feedback

50. **Test end-to-end workflow**
    - Test complete workflow: acquire → place → manage → work
    - Test edge cases (mood drops, quota removal, etc.)
    - Validation: Complete system works end-to-end

51. **Performance optimization**
    - Optimize need checking frequency
    - Optimize quota seeking and selection
    - Optimize mood decay calculations
    - Validation: System performs well with multiple NPCs

52. **Documentation and cleanup**
    - Document system behavior and interactions
    - Clean up temporary code and debug output
    - Validation: Code is clean and documented
