using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ValheimVillages.NPCs
{
    /// <summary>
    /// Configures NPC equipment and appearance from JSON definitions.
    /// Called after prefab instantiation but before Humanoid.Start() runs.
    /// </summary>
    public static class NpcEquipment
    {
        /// <summary>Default skin tone applied to all villagers (warm human color).</summary>
        private static readonly Vector3 DefaultSkinColor = new(1.15f, 0.85f, 0.55f);

        /// <summary>
        /// Select the base prefab for an NPC based on its definition.
        /// If preferredPrefab is set, use that; otherwise pick randomly from allPrefabs.
        /// </summary>
        public static string SelectPrefab(NpcTypeDefinition definition, string[] allPrefabs)
        {
            if (!string.IsNullOrEmpty(definition?.preferredPrefab))
                return definition.preferredPrefab;

            return allPrefabs[UnityEngine.Random.Range(0, allPrefabs.Length)];
        }

        /// <summary>
        /// Configure NPC equipment from its type definition. Clears random equipment pools
        /// and sets m_defaultItems to the prefabs listed in the definition's equipment array.
        /// </summary>
        public static void Configure(Humanoid humanoid, NpcTypeDefinition definition)
        {
            humanoid.m_randomWeapon = Array.Empty<GameObject>();
            humanoid.m_randomArmor = Array.Empty<GameObject>();
            humanoid.m_randomShield = Array.Empty<GameObject>();
            humanoid.m_randomSets = Array.Empty<Humanoid.ItemSet>();
            humanoid.m_randomItems = Array.Empty<Humanoid.RandomItem>();

            if (definition?.equipment == null || definition.equipment.Count == 0 ||
                ZNetScene.instance == null)
            {
                humanoid.m_defaultItems = Array.Empty<GameObject>();
                return;
            }

            var items = new List<GameObject>();
            foreach (var prefabName in definition.equipment)
            {
                if (string.IsNullOrEmpty(prefabName)) continue;

                var prefab = ZNetScene.instance.GetPrefab(prefabName);
                if (prefab != null)
                    items.Add(prefab);
                else
                    Plugin.Log?.LogWarning($"Equipment prefab '{prefabName}' not found");
            }

            humanoid.m_defaultItems = items.ToArray();
            Plugin.Log?.LogInfo(
                $"Configured {definition.displayName} with {items.Count} equipment items: " +
                string.Join(", ", definition.equipment));
        }

        /// <summary>
        /// Apply skin color from the definition, or a default human skin tone.
        /// Writes to VisEquipment which persists via ZDO.
        /// </summary>
        public static void ApplySkinColor(VisEquipment visEquip, NpcTypeDefinition definition)
        {
            if (visEquip == null) return;

            var color = DefaultSkinColor;
            if (!string.IsNullOrEmpty(definition?.skinColor) &&
                TryParseVector3(definition.skinColor, out var parsed))
            {
                color = parsed;
            }

            visEquip.SetSkinColor(color);
            Plugin.Log?.LogInfo($"Applied skin color ({color.x:F2}, {color.y:F2}, {color.z:F2})");
        }

        /// <summary>
        /// Add weapon rotation fix component if the definition specifies one.
        /// </summary>
        public static void ApplyWeaponRotationFix(GameObject npcObject, NpcTypeDefinition definition)
        {
            if (string.IsNullOrEmpty(definition?.weaponRotationFix)) return;
            if (!TryParseVector3(definition.weaponRotationFix, out var euler)) return;

            var fix = npcObject.AddComponent<NpcVisualFix>();
            fix.Initialize(euler);
            Plugin.Log?.LogInfo($"Applied weapon rotation fix ({euler.x}, {euler.y}, {euler.z})");
        }

        /// <summary>
        /// Override the NpcTalk component's dialog lines from the definition.
        /// Only replaces lists that have entries in the JSON; empty lists keep defaults.
        /// </summary>
        public static void ConfigureDialog(GameObject npcObject, NpcTypeDefinition definition)
        {
            if (definition == null) return;

            var npcTalk = npcObject.GetComponent<NpcTalk>();
            if (npcTalk == null) return;

            if (definition.randomTalk?.Count > 0)
                npcTalk.m_randomTalk = new List<string>(definition.randomTalk);

            if (definition.randomGreets?.Count > 0)
                npcTalk.m_randomGreets = new List<string>(definition.randomGreets);

            if (definition.randomGoodbye?.Count > 0)
                npcTalk.m_randomGoodbye = new List<string>(definition.randomGoodbye);

            // Village NPCs shouldn't yell ward alarms or aggravated lines
            npcTalk.m_privateAreaAlarm = new List<string>();
            npcTalk.m_aggravated = new List<string>();

            Plugin.Log?.LogInfo($"Configured dialog for {definition.displayName}");
        }

        private static bool TryParseVector3(string csv, out Vector3 result)
        {
            result = Vector3.zero;
            if (string.IsNullOrEmpty(csv)) return false;

            var parts = csv.Split(',');
            if (parts.Length != 3) return false;

            if (float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            {
                result = new Vector3(x, y, z);
                return true;
            }
            return false;
        }
    }
}
