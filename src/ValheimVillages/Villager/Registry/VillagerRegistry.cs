using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Schemas;

namespace ValheimVillages.Villager.Registry
{
    /// <summary>
    ///     Registry of villager type definitions loaded from embedded JSON files.
    /// </summary>
    public static class VillagerRegistry
    {
        private static Dictionary<string, VillagerDef> _definitions;

        /// <summary>
        ///     Get all villager type definitions.
        /// </summary>
        public static IReadOnlyDictionary<string, VillagerDef> Definitions
        {
            get
            {
                EnsureInitialized();
                return _definitions;
            }
        }

        /// <summary>
        ///     Get a specific villager type definition.
        /// </summary>
        public static VillagerDef Get(string villagerType)
        {
            EnsureInitialized();
            return _definitions.TryGetValue(villagerType, out var def) ? def : null;
        }

        private static void EnsureInitialized()
        {
            if (_definitions != null) return;

            _definitions = new Dictionary<string, VillagerDef>();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.Contains(".Villager.Registry.Definitions.") || !resourceName.EndsWith(".json"))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    using var reader = new StreamReader(stream);
                    var def = JsonUtility.FromJson<VillagerDef>(reader.ReadToEnd());

                    if (!string.IsNullOrEmpty(def?.type))
                    {
                        _definitions[def.type] = def;
                        Plugin.Log?.LogInfo($"Loaded villager: {def.displayName} ({def.category})");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"Failed to load villager definition {resourceName}: {ex.Message}");
                }
            }

            Plugin.Log?.LogInfo($"VillagerRegistry initialized with {_definitions.Count} types");
        }
    }
}