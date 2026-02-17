# Design: ItemManager-Style Registration

## Overview

This design documents the registration pattern derived from Azumatt's ItemManager that integrates custom items with Valheim's ObjectDB and ZNetScene systems.

## Valheim Item System Architecture

### ObjectDB

The `ObjectDB` singleton manages all item prefabs in the game:

- `m_items`: List of all item GameObjects
- `m_itemByHash`: Private dictionary mapping stable hash codes to GameObjects
- `GetItemPrefab(string/int)`: Retrieves prefabs by name or hash

**Lifecycle Events**:
- `Awake()`: Called when ObjectDB initializes, items are loaded from game data
- `CopyOtherDB()`: Called to copy items from another ObjectDB instance (server sync)

### ZNetScene

The `ZNetScene` singleton manages network-spawnable prefabs:

- `m_prefabs`: List of all prefabs that can be spawned
- `m_namedPrefabs`: Private dictionary mapping stable hash codes to GameObjects
- `GetPrefab(int)`: Retrieves prefabs by hash

**Lifecycle Events**:
- `Awake()`: Called when ZNetScene initializes

## Registration Pattern

### Patch Priority

All patches use `[HarmonyPriority(Priority.VeryLow)]` to ensure:
1. Vanilla game code runs first
2. Other mods' patches run before ours
3. We can safely reference items added by other mods

### Registration Flow

```
Game Start
    │
    ▼
ObjectDB.Awake() ─────────────► RegisterItems()
    │                                │
    │                                ▼
    │                          For each JSON definition:
    │                            1. Clone base prefab
    │                            2. Configure item properties
    │                            3. Add to ObjectDB.m_items
    │                            4. Add to ObjectDB.m_itemByHash
    │
    ▼
ObjectDB.CopyOtherDB() ───────► RegisterItems() (re-register after sync)
    │
    ▼
ZNetScene.Awake() ────────────► EnsureZNetScene()
                                     │
                                     ▼
                               For each registered prefab:
                                 1. Add to ZNetScene.m_prefabs
                                 2. Add to ZNetScene.m_namedPrefabs
```

### Prefab Creation

For clone-based items (current approach):

1. Get base prefab from ObjectDB: `objectDB.GetItemPrefab(def.basePrefab)`
2. Instantiate clone: `Object.Instantiate(basePrefab)`
3. Rename to unique name: `prefab.name = def.name`
4. Set hide flags: `prefab.hideFlags = HideFlags.HideAndDontSave`
5. Configure ItemDrop component with JSON values

### Hash Registration

Both ObjectDB and ZNetScene use Valheim's stable hash:

```csharp
int hash = prefab.name.GetStableHashCode();
```

Private dictionaries must be accessed via reflection:
- `ObjectDB.m_itemByHash`
- `ZNetScene.m_namedPrefabs`

## JSON-Driven Configuration

The existing `ItemDefinition` class maps JSON fields to item properties:

| JSON Field | ItemDrop.ItemData.m_shared Field |
|------------|----------------------------------|
| displayName | m_name |
| description | m_description |
| maxStackSize | m_maxStackSize |
| weight | m_weight |
| variants | m_variants |

## Error Handling

Per user preference, use fail-fast approach:
- Log errors for missing base prefabs
- Do not implement recreation/caching
- Errors surface immediately for debugging

## File Changes

| File | Changes |
|------|---------|
| `Patches/ItemPatch.cs` | Add Harmony patches with Priority.VeryLow for ObjectDB and ZNetScene |
| `Items/ItemFactory.cs` | Add `RegisterAll()`, `EnsureAllInZNetScene()`, helper methods |

