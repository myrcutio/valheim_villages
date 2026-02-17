# Tasks: ItemManager-Style Registration

## Task List

### 1. Add ObjectDB registration patches
- [x] Add `[HarmonyPatch(typeof(ObjectDB), "Awake")]` with `[HarmonyPriority(Priority.VeryLow)]`
- [x] Add `[HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]` with `[HarmonyPriority(Priority.VeryLow)]`
- [x] Both patches call `ItemFactory.RegisterAll(ObjectDB __instance)`

**File**: `src/ValheimVillages/Patches/ItemPatch.cs`

**Verify**: Build succeeds, patches are declared correctly

---

### 2. Add ZNetScene registration patch
- [x] Add `[HarmonyPatch(typeof(ZNetScene), "Awake")]` with `[HarmonyPriority(Priority.VeryLow)]`
- [x] Patch calls `ItemFactory.EnsureAllInZNetScene()`

**File**: `src/ValheimVillages/Patches/ItemPatch.cs`

**Verify**: Build succeeds, patch is declared correctly

---

### 3. Implement RegisterAll method
- [x] Add `RegisterAll(ObjectDB objectDB)` method to ItemFactory
- [x] Early return if ObjectDB not ready (m_items null or empty)
- [x] Iterate JSON definitions and call RegisterItem for each

**File**: `src/ValheimVillages/Items/ItemFactory.cs`

**Verify**: Build succeeds

---

### 4. Implement RegisterItem method
- [x] Add private `RegisterItem(ObjectDB objectDB, ItemDefinition def)` method
- [x] Skip if already registered and valid in ObjectDB
- [x] Call CreateAndRegister if not registered

**File**: `src/ValheimVillages/Items/ItemFactory.cs`

**Verify**: Build succeeds

---

### 5. Implement CreateAndRegister method
- [x] Add private `CreateAndRegister(ObjectDB objectDB, ItemDefinition def)` method
- [x] Get base prefab from ObjectDB
- [x] Clone prefab, set name and hideFlags
- [x] Configure ItemDrop component from JSON definition
- [x] Add to ObjectDB.m_items list
- [x] Add to ObjectDB.m_itemByHash via reflection
- [x] Add to ZNetScene if available
- [x] Log success

**File**: `src/ValheimVillages/Items/ItemFactory.cs`

**Verify**: Build succeeds

---

### 6. Implement EnsureAllInZNetScene method
- [x] Add `EnsureAllInZNetScene()` method to ItemFactory
- [x] Early return if ZNetScene.instance is null
- [x] Iterate registered prefabs and add each to ZNetScene

**File**: `src/ValheimVillages/Items/ItemFactory.cs`

**Verify**: Build succeeds

---

### 7. Implement AddToZNetScene helper
- [x] Add private `AddToZNetScene(ZNetScene zns, GameObject prefab)` method
- [x] Check if prefab already registered by hash
- [x] Add to m_prefabs list if not present
- [x] Add to m_namedPrefabs dictionary via reflection

**File**: `src/ValheimVillages/Items/ItemFactory.cs`

**Verify**: Build succeeds

---

### 8. Test in-game item registration
- [ ] Launch game with mod loaded (manual test by user)
- [ ] Verify vv_pawn item appears in spawn menu (if debug enabled)
- [ ] Spawn item and verify properties match JSON definition
- [ ] Verify item can be picked up and dropped

**Verify**: Item functions correctly in-game

---

## Dependencies

- Tasks 1-2 can be done in parallel
- Tasks 3-7 can be done in sequence after tasks 1-2
- Task 8 requires all prior tasks complete

