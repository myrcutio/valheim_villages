using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Items.Icons;

namespace ValheimVillages.Items
{
    /// <summary>
    /// Factory for creating and registering custom items from embedded JSON definitions.
    /// </summary>
    public static class ItemFactory
    {
        private static readonly List<GameObject> _prefabs = new();
        private static List<ItemDefinition> _definitions;

        /// <summary>
        /// Register all items from embedded JSON definitions.
        /// Called from ObjectDB.Awake and ObjectDB.CopyOtherDB patches.
        /// </summary>
        public static void RegisterAll(ObjectDB objectDB)
        {
            if (objectDB?.m_items == null || objectDB.m_items.Count == 0)
            {
                Plugin.Log?.LogWarning("ObjectDB not ready");
                return;
            }

            foreach (var def in GetDefinitions())
            {
                CreatePrefabIfNeeded(objectDB, def);
            }

            var itemByHash = GetPrivateDictionary(objectDB, "m_itemByHash");
            foreach (var prefab in _prefabs)
            {
                AddToCollection(objectDB.m_items, prefab);
                AddToHashMap(itemByHash, prefab);
            }
            
            Plugin.Log?.LogInfo($"Registered {_prefabs.Count} custom items in ObjectDB");
        }

        /// <summary>
        /// Ensure all registered prefabs are in ZNetScene.
        /// Called from ZNetScene.Awake patch.
        /// </summary>
        public static void RegisterAllInZNetScene(ZNetScene instance)
        {
            var namedPrefabs = GetPrivateDictionary(instance, "m_namedPrefabs");
            foreach (var prefab in _prefabs)
            {
                AddToCollection(instance.m_prefabs, prefab);
                AddToHashMap(namedPrefabs, prefab);
            }
            
            Plugin.Log?.LogInfo($"Registered {_prefabs.Count} prefabs in ZNetScene");
        }

        /// <summary>
        /// Get all item definitions from embedded JSON files.
        /// Only loads resources under the Items namespace (filters out NPC definitions).
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

            return _definitions;
        }

        /// <summary>
        /// Get item definitions filtered by itemType (e.g., "fragment", "workorder").
        /// </summary>
        public static List<ItemDefinition> GetDefinitionsByType(string itemType)
        {
            var results = new List<ItemDefinition>();
            foreach (var def in GetDefinitions())
            {
                if (def.itemType == itemType)
                    results.Add(def);
            }
            return results;
        }

        /// <summary>
        /// Find a single item definition by name.
        /// </summary>
        public static ItemDefinition GetDefinition(string name)
        {
            foreach (var def in GetDefinitions())
            {
                if (def.name == name)
                    return def;
            }
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
            bool wasActive = basePrefab.activeSelf;
            basePrefab.SetActive(false);
            
            var prefab = UnityEngine.Object.Instantiate(basePrefab);
            
            basePrefab.SetActive(wasActive);
            prefab.name = newName;
            UnityEngine.Object.DontDestroyOnLoad(prefab);
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

            // Apply custom icon for work orders from embedded PNG resources
            if (def.itemType == "workorder" && !string.IsNullOrEmpty(def.stationType))
            {
                var icon = WorkOrderIconLoader.Load(def.stationType);
                if (icon != null)
                    newShared.m_icons = new[] { icon };
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
            if (!list.Contains(prefab))
                list.Add(prefab);
        }

        private static void AddToHashMap(Dictionary<int, GameObject> hashMap, GameObject prefab)
        {
            if (hashMap == null) return;
            int hash = prefab.name.GetStableHashCode();
            hashMap[hash] = prefab;
        }
    }
}
