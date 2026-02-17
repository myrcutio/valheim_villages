using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.NPCs
{
    /// <summary>
    /// Registry of NPC type definitions loaded from embedded JSON files.
    /// </summary>
    public static class NpcTypeRegistry
    {
        private static Dictionary<NpcType, NpcTypeDefinition> _definitions;

        /// <summary>
        /// Get all NPC type definitions.
        /// </summary>
        public static IReadOnlyDictionary<NpcType, NpcTypeDefinition> Definitions
        {
            get
            {
                EnsureInitialized();
                return _definitions;
            }
        }

        /// <summary>
        /// Get a specific NPC type definition.
        /// </summary>
        public static NpcTypeDefinition Get(NpcType type)
        {
            EnsureInitialized();
            return _definitions.TryGetValue(type, out var def) ? def : null;
        }

        /// <summary>
        /// Get all NPC types in a specific category.
        /// </summary>
        public static IEnumerable<NpcTypeDefinition> GetByCategory(NpcCategory category)
        {
            EnsureInitialized();
            foreach (var def in _definitions.Values)
            {
                if (def.GetCategory() == category)
                    yield return def;
            }
        }

        /// <summary>
        /// Check if an NPC type is a Villager (production-focused).
        /// </summary>
        public static bool IsVillager(NpcType type) => Get(type)?.GetCategory() == NpcCategory.Villager;

        /// <summary>
        /// Check if an NPC type is a Specialist (direct benefits).
        /// </summary>
        public static bool IsSpecialist(NpcType type) => Get(type)?.GetCategory() == NpcCategory.Specialist;

        private static void EnsureInitialized()
        {
            if (_definitions != null) return;

            _definitions = new Dictionary<NpcType, NpcTypeDefinition>();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.Contains(".NPCs.Definitions.") || !resourceName.EndsWith(".json"))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    using var reader = new StreamReader(stream);
                    var def = JsonUtility.FromJson<NpcTypeDefinition>(reader.ReadToEnd());

                    if (!string.IsNullOrEmpty(def?.type))
                    {
                        _definitions[def.GetNpcType()] = def;
                        Plugin.Log?.LogInfo($"Loaded NPC type: {def.displayName} ({def.category})");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError($"Failed to load NPC definition {resourceName}: {ex.Message}");
                }
            }

            Plugin.Log?.LogInfo($"NpcTypeRegistry initialized with {_definitions.Count} NPC types");
        }
    }
}
