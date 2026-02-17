# Item Registration Specification

## ADDED Requirements

### Requirement: ObjectDB item registration

The system SHALL register custom items with ObjectDB when the database initializes.

#### Scenario: Items registered on ObjectDB.Awake

- **Given** the game is starting and ObjectDB.Awake is called
- **And** ObjectDB has loaded vanilla items (m_items.Count > 0)
- **When** the Harmony postfix patch executes
- **Then** all JSON-defined items are created and registered with ObjectDB

#### Scenario: Items re-registered on ObjectDB.CopyOtherDB

- **Given** the player connects to a server
- **And** ObjectDB.CopyOtherDB is called to sync server data
- **When** the Harmony postfix patch executes
- **Then** all JSON-defined items are re-registered with ObjectDB

#### Scenario: Items added to ObjectDB hash map

- **Given** a custom item prefab is created
- **When** the item is registered with ObjectDB
- **Then** the item is added to ObjectDB.m_itemByHash using its stable hash code
- **And** the item can be retrieved via ObjectDB.GetItemPrefab(name)

---

### Requirement: ZNetScene prefab registration

The system SHALL register custom prefabs with ZNetScene for network spawning.

#### Scenario: Prefabs registered on ZNetScene.Awake

- **Given** ZNetScene is initializing
- **When** the Harmony postfix patch executes
- **Then** all registered item prefabs are added to ZNetScene

#### Scenario: Prefabs added to ZNetScene hash map

- **Given** a custom item prefab is registered
- **When** the prefab is added to ZNetScene
- **Then** the prefab is added to ZNetScene.m_prefabs list
- **And** the prefab is added to ZNetScene.m_namedPrefabs dictionary
- **And** the prefab can be retrieved via ZNetScene.GetPrefab(hash)

---

### Requirement: Prefab creation from JSON definitions

The system SHALL create item prefabs by cloning base prefabs and applying JSON configuration.

#### Scenario: Clone-based prefab creation

- **Given** a JSON item definition with source="clone" and basePrefab="DragonEgg"
- **When** the item is registered
- **Then** the DragonEgg prefab is cloned
- **And** the clone is renamed to the JSON-defined name
- **And** the clone's hideFlags are set to HideAndDontSave

#### Scenario: Item properties applied from JSON

- **Given** a JSON item definition with displayName, description, maxStackSize, weight, and variants
- **When** the prefab is created
- **Then** the ItemDrop.m_itemData.m_shared fields are set to the JSON values

#### Scenario: Missing base prefab logs error

- **Given** a JSON item definition references a non-existent basePrefab
- **When** registration is attempted
- **Then** an error is logged
- **And** the item is not registered

---

### Requirement: Harmony patch priority

All registration patches SHALL use low priority to ensure mod compatibility.

#### Scenario: Patches run after other mods

- **Given** multiple mods patch ObjectDB.Awake
- **When** patches execute
- **Then** ValheimVillages patches run after higher-priority patches
- **And** ValheimVillages can reference items added by other mods

---

### Requirement: Idempotent registration

The system SHALL safely handle multiple registration attempts.

#### Scenario: Duplicate registration prevented

- **Given** an item has already been registered with ObjectDB
- **When** registration is triggered again (e.g., scene reload)
- **Then** the existing registration is reused
- **And** no duplicate entries are created

