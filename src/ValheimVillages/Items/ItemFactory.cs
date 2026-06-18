using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Items.Icons;
using ValheimVillages.Villager.Registry;
using Object = UnityEngine.Object;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Factory for creating and registering custom items from embedded JSON definitions.
    /// </summary>
    public static class ItemFactory
    {
        private static readonly List<GameObject> _prefabs = new();
        private static List<ItemDefinition> _definitions;

        /// <summary>
        ///     Physical Valheim station → (work order key, display name, stationType for icon).
        ///     These are engine constants; virtual station work orders come from VillagerRegistry.
        /// </summary>
        private static readonly (string station, string key, string displayName, string stationType)[]
            PhysicalStations =
            {
                ("$piece_workbench", "workbench", "Workbench", "Workbench"),
                ("$piece_forge", "forge", "Forge", "Forge"),
                ("$piece_cauldron", "cauldron", "Cauldron", "Cauldron"),
                ("$piece_artisanstation", "artisan", "Artisan Table", "ArtisanTable"),
                ("$piece_stonecutter", "stonecutter", "Stonecutter", "Stonecutter"),
            };

        /// <summary>
        ///     Biome fragments: (Heightmap.Biome enum, item key, biome ID, display name, ink color).
        ///     Used to generate fragment items and the BiomeFragmentMap for loot injection.
        /// </summary>
        public static readonly (int biomeEnum, string key, string biomeId, string displayName, string inkColor)[]
            FragmentBiomes =
            {
                ((int)Heightmap.Biome.Meadows, "meadows", "Meadows", "Meadows", "green"),
                ((int)Heightmap.Biome.BlackForest, "blackforest", "BlackForest", "Black Forest", "dark blue"),
                ((int)Heightmap.Biome.Swamp, "swamp", "Swamp", "Swamp", "sickly brown"),
                ((int)Heightmap.Biome.Mountain, "mountains", "Mountain", "Mountains", "blue"),
                ((int)Heightmap.Biome.Plains, "plains", "Plains", "Plains", "golden"),
                ((int)Heightmap.Biome.Mistlands, "mistlands", "Mistlands", "Mistlands", "purple"),
                ((int)Heightmap.Biome.AshLands, "ashlands", "Ashlands", "Ashlands", "crimson"),
            };

        /// <summary>
        ///     Register all items from embedded JSON definitions.
        ///     Called from ObjectDB.Awake and ObjectDB.CopyOtherDB patches.
        /// </summary>
        public static void RegisterAll(ObjectDB objectDB)
        {
            if (objectDB?.m_items == null || objectDB.m_items.Count == 0)
            {
                Plugin.Log?.LogWarning("ObjectDB not ready");
                return;
            }

            foreach (var def in GetDefinitions()) CreatePrefabIfNeeded(objectDB, def);

            // Drop dead (destroyed) entries left by earlier reloads before re-adding. A single
            // fake-null in m_items makes ObjectDB.GetAllItems throw every frame (the
            // PlayerCustomizaton.LoadHair NRE storm); a dead m_itemByHash value does the same.
            var purged = _prefabs.RemoveAll(p => p == null)
                         + objectDB.m_items.RemoveAll(go => go == null);

            var itemByHash = GetPrivateDictionary(objectDB, "m_itemByHash");
            if (itemByHash != null)
            {
                var deadHashes = new List<int>();
                foreach (var kv in itemByHash)
                    if (kv.Value == null) deadHashes.Add(kv.Key);
                foreach (var h in deadHashes) itemByHash.Remove(h);
                purged += deadHashes.Count;
            }

            foreach (var prefab in _prefabs)
            {
                AddToCollection(objectDB.m_items, prefab);
                AddToHashMap(itemByHash, prefab);
            }

            Plugin.Log?.LogInfo(
                $"Registered {_prefabs.Count} custom items in ObjectDB (purged {purged} dead entries)");

            // ZNetScene.Awake can fire BEFORE this runs on a world load (observed:
            // "Registered 0 prefabs in ZNetScene" — _prefabs was empty/destroyed at that point),
            // so the prefabs end up in ObjectDB but never in ZNetScene, and ZNetScene.GetPrefab(...)
            // returns null (symptom: "Failed to create work order"). Register into ZNetScene here too
            // whenever it's available so creation and ZNetScene registration are order-independent.
            // RegisterAllInZNetScene is idempotent.
            if (ZNetScene.instance != null)
                RegisterAllInZNetScene(ZNetScene.instance);
        }

        /// <summary>
        ///     Ensure all registered prefabs are in ZNetScene.
        ///     Called from ZNetScene.Awake patch.
        /// </summary>
        public static void RegisterAllInZNetScene(ZNetScene instance)
        {
            _prefabs.RemoveAll(p => p == null);
            instance.m_prefabs.RemoveAll(go => go == null);

            var namedPrefabs = GetPrivateDictionary(instance, "m_namedPrefabs");
            if (namedPrefabs != null)
            {
                var deadHashes = new List<int>();
                foreach (var kv in namedPrefabs)
                    if (kv.Value == null) deadHashes.Add(kv.Key);
                foreach (var h in deadHashes) namedPrefabs.Remove(h);
            }

            foreach (var prefab in _prefabs)
            {
                AddToCollection(instance.m_prefabs, prefab);
                AddToHashMap(namedPrefabs, prefab);
            }

            Plugin.Log?.LogInfo($"Registered {_prefabs.Count} prefabs in ZNetScene");
        }

        /// <summary>
        ///     Get all item definitions from embedded JSON files.
        ///     Only loads resources under the Items namespace (filters out NPC definitions).
        /// </summary>
        public static List<ItemDefinition> GetDefinitions()
        {
            if (_definitions != null) return _definitions;

            _definitions = new List<ItemDefinition>();
            var assembly = Assembly.GetExecutingAssembly();
            var itemResourcePrefix = typeof(ItemDefinition).Namespace?.Replace('.', '_')
                                     ?? "ValheimVillages.Items";

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.EndsWith(".json")) continue;

                // Only load item definitions, not NPC definitions
                if (!resourceName.Contains(".Items."))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    using var reader = new StreamReader(stream);
                    var def = JsonUtility.FromJson<ItemDefinition>(reader.ReadToEnd());

                    if (!string.IsNullOrEmpty(def?.name) && def.source == "clone")
                        _definitions.Add(def);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"Failed to load {resourceName}: {ex.Message}");
                }
            }

            GenerateRegistryItems(_definitions);

            return _definitions;
        }

        /// <summary>
        ///     Returns the station name → work order item name mapping for all stations
        ///     (physical + virtual). Used by CraftingStationPatch to build its lookup.
        /// </summary>
        public static Dictionary<string, string> BuildStationWorkOrderMap()
        {
            var map = new Dictionary<string, string>();
            foreach (var (station, key, _, _) in PhysicalStations)
                map[station] = $"vv_workorder_{key}";
            foreach (var kv in VillagerRegistry.Definitions)
                if (!string.IsNullOrEmpty(kv.Value.stationName))
                    map[kv.Value.stationName] = $"vv_workorder_{kv.Key.ToLower()}";
            return map;
        }

        /// <summary>
        ///     Generates all programmatic item definitions: pawns and work orders.
        /// </summary>
        private static void GenerateRegistryItems(List<ItemDefinition> definitions)
        {
            // Generic pawn (random villager type)
            AddIfMissing(definitions, new ItemDefinition
            {
                name = "vv_pawn",
                source = "clone",
                basePrefab = "DragonEgg",
                displayName = "Villager",
                description = "A villager packaged for transport.",
                maxStackSize = 1,
                weight = 10f,
                itemType = "pawn",
            });

            // Per-type pawns + virtual station work orders from VillagerRegistry
            foreach (var kv in VillagerRegistry.Definitions)
            {
                var def = kv.Value;
                var typeKey = def.type.ToLower();

                AddIfMissing(definitions, new ItemDefinition
                {
                    name = $"vv_{typeKey}_pawn",
                    source = "clone",
                    basePrefab = "DragonEgg",
                    displayName = def.displayName,
                    description = $"A {def.displayName.ToLower()} villager packaged for transport. {def.description}",
                    maxStackSize = 1,
                    weight = 10f,
                    itemType = "pawn",
                });

                if (!string.IsNullOrEmpty(def.stationName))
                    AddIfMissing(definitions, new ItemDefinition
                    {
                        name = $"vv_workorder_{typeKey}",
                        source = "clone",
                        basePrefab = "Wood",
                        displayName = $"{def.displayName} Work Order",
                        description =
                            $"A work order scroll for {def.displayName.ToLower()} tasks. Right-click to set production quotas.",
                        maxStackSize = 1,
                        weight = 0.3f,
                        itemType = "workorder",
                        stationType = def.type,
                    });
            }

            // Physical station work orders
            foreach (var (_, key, displayName, stationType) in PhysicalStations)
                AddIfMissing(definitions, new ItemDefinition
                {
                    name = $"vv_workorder_{key}",
                    source = "clone",
                    basePrefab = "Wood",
                    displayName = $"{displayName} Work Order",
                    description =
                        $"A work order scroll for {displayName.ToLower()} tasks. Right-click to set production quotas.",
                    maxStackSize = 1,
                    weight = 0.3f,
                    itemType = "workorder",
                    stationType = stationType,
                });

            // Biome fragment items
            foreach (var (_, key, biomeId, displayName, inkColor) in FragmentBiomes)
                AddIfMissing(definitions, new ItemDefinition
                {
                    name = $"vv_fragment_{key}",
                    source = "clone",
                    basePrefab = "DragonEgg",
                    displayName = $"{displayName} Ransom Fragment",
                    description = $"A torn piece of parchment stained with {inkColor} ink. " +
                                  $"The scrawled text hints at a captive held somewhere in the {displayName.ToLower()}. " +
                                  "Combine three to reveal their location.",
                    maxStackSize = 3,
                    weight = 0.1f,
                    itemType = "fragment",
                    biome = biomeId,
                });
        }

        private static void AddIfMissing(List<ItemDefinition> defs, ItemDefinition def)
        {
            if (!defs.Exists(d => d.name == def.name))
                defs.Add(def);
        }

        /// <summary>
        ///     Get item definitions filtered by itemType (e.g., "fragment", "workorder").
        /// </summary>
        public static List<ItemDefinition> GetDefinitionsByType(string itemType)
        {
            var results = new List<ItemDefinition>();
            foreach (var def in GetDefinitions())
                if (def.itemType == itemType)
                    results.Add(def);
            return results;
        }

        /// <summary>
        ///     Find a single item definition by name.
        /// </summary>
        public static ItemDefinition GetDefinition(string name)
        {
            foreach (var def in GetDefinitions())
                if (def.name == name)
                    return def;
            return null;
        }

        private static void CreatePrefabIfNeeded(ObjectDB objectDB, ItemDefinition def)
        {
            // Skip if already created and still valid
            if (_prefabs.Exists(p => p != null && p.name == def.name))
                return;

            // Check if ObjectDB already has this item (hot reload case)
            var existing = objectDB.GetItemPrefab(def.name);
            if (existing != null)
            {
                // Re-apply definition so hot-reloaded code changes (icons, etc.) take effect
                ApplyItemDefinition(existing, def);
                if (!_prefabs.Contains(existing))
                    _prefabs.Add(existing);
                return;
            }

            var basePrefab = objectDB.GetItemPrefab(def.basePrefab);
            if (basePrefab == null)
            {
                Plugin.Log?.LogError($"Base prefab '{def.basePrefab}' not found for item '{def.name}'");
                return;
            }

            var prefab = ClonePrefab(basePrefab, def.name);
            ApplyItemDefinition(prefab, def);

            _prefabs.Add(prefab);
            Plugin.Log?.LogInfo($"Created custom item: {def.name}");
        }

        private static GameObject ClonePrefab(GameObject basePrefab, string newName)
        {
            var wasActive = basePrefab.activeSelf;
            basePrefab.SetActive(false);

            var prefab = Object.Instantiate(basePrefab);

            basePrefab.SetActive(wasActive);
            prefab.name = newName;
            // Park the template under the shared inactive root so it never renders/awakes at
            // world origin; activeSelf stays true so ZNetScene/placement clones come out active.
            prefab.transform.SetParent(PrefabTemplates.Root, false);
            prefab.SetActive(true);

            return prefab;
        }

        private static void ApplyItemDefinition(GameObject prefab, ItemDefinition def)
        {
            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null) return;

            // Deep-copy SharedData to break reference to original asset
            var originalShared = itemDrop.m_itemData.m_shared;
            var newShared = JsonUtility.FromJson<ItemDrop.ItemData.SharedData>(
                JsonUtility.ToJson(originalShared));

            newShared.m_name = def.displayName;
            newShared.m_description = def.description;
            newShared.m_maxStackSize = def.maxStackSize;
            newShared.m_weight = def.weight;
            newShared.m_variants = def.variants;

            // Apply custom icon for work orders from embedded PNG resources, and
            // reskin the world model into a flat parchment sheet matching the icon.
            if (def.itemType == "workorder" && !string.IsNullOrEmpty(def.stationType))
            {
                var icon = WorkOrderIconLoader.Load(def.stationType);
                if (icon != null)
                    newShared.m_icons = new[] { icon };

                var tex = WorkOrderIconLoader.LoadTexture(def.stationType);
                if (tex != null)
                    ParchmentModel.Apply(prefab, tex);
            }

            itemDrop.m_itemData.m_shared = newShared;
            itemDrop.m_itemData.m_dropPrefab = prefab;
        }

        private static Dictionary<int, GameObject> GetPrivateDictionary<T>(T instance, string fieldName)
        {
            var field = typeof(T).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(instance) as Dictionary<int, GameObject>;
        }

        private static void AddToCollection(List<GameObject> list, GameObject prefab)
        {
            // Unity '==' catches a destroyed ("fake-null") GameObject; List.Contains uses
            // reference equality and does NOT, so a dead template would otherwise be re-added
            // and then throw every frame in ObjectDB.GetAllItems / ZNetScene lookups.
            if (prefab == null) return;
            if (!list.Contains(prefab))
                list.Add(prefab);
        }

        private static void AddToHashMap(Dictionary<int, GameObject> hashMap, GameObject prefab)
        {
            if (hashMap == null || prefab == null) return;
            var hash = prefab.name.GetStableHashCode();
            hashMap[hash] = prefab;
        }
    }
}