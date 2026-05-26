using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Random = UnityEngine.Random;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Handles combining 3 same-biome ransom note fragments into a rescue quest map marker.
    /// </summary>
    public static class FragmentCombiner
    {
        private const int RequiredFragments = 3;

        /// <summary>
        ///     Maps biome identifiers to the NPC type that can be rescued there.
        /// </summary>
        private static readonly Dictionary<string, string> BiomeNpcMap = new()
        {
            { "Meadows", "Farmer" },
            { "BlackForest", "Miner" },
            { "Swamp", "Miner" },
            { "Mountain", "Mountaineer" },
            { "Plains", "Farmer" },
            { "Mistlands", "Scout" },
            { "Ashlands", "Guard" },
        };

        /// <summary>
        ///     Maps biome identifiers to dungeon/location prefab names that can host rescue quests.
        ///     These are locations that are guaranteed to spawn in the correct biome and have
        ///     appropriate enemy difficulty. Preferred locations are listed first.
        /// </summary>
        private static readonly Dictionary<string, string[]> BiomeLocationMap = new()
        {
            { "Meadows", new[] { "Dolmen01", "Dolmen02", "Dolmen03", "WoodVillage1" } },
            {
                "BlackForest", new[]
                {
                    "TrollCave", "TrollCave02", "Crypt2", "Crypt3", "Crypt4",
                    "Hildir_crypt",
                }
            },
            {
                "Swamp", new[]
                {
                    "SunkenCrypt1", "SunkenCrypt2", "SunkenCrypt3", "SunkenCrypt4",
                    "SwampRuin1", "SwampRuin2",
                }
            },
            {
                "Mountain", new[]
                {
                    "MountainCave01", "MountainCave02", "Hildir_cave",
                    "AbandonedLogCabin02", "AbandonedLogCabin03",
                    "AbandonedLogCabin04",
                }
            },
            {
                "Plains", new[]
                {
                    "GoblinCamp1", "GoblinCamp2", "StoneTower1", "StoneTower2",
                    "StoneTower3", "StoneTower4", "Hildir_plainsfortress",
                }
            },
            {
                "Mistlands", new[]
                {
                    "Mistlands_GuardTower1_new", "Mistlands_GuardTower2_new",
                    "Mistlands_GuardTower3_new", "Mistlands_Excavation1",
                    "Mistlands_Excavation2", "Mistlands_Excavation3",
                }
            },
            {
                "Ashlands", new[]
                {
                    "CharredFortress", "FortressRuins", "CharredRuins1",
                    "CharredTowerRuins1", "CharredTowerRuins2",
                    "CharredTowerRuins3",
                }
            },
        };

        /// <summary>
        ///     Attempts to combine 3 fragments of the given biome from the player's inventory.
        ///     Counts stack sizes so a single stack of 3 is sufficient.
        ///     On success, consumes the fragments and places a rescue quest marker on the map.
        /// </summary>
        /// <returns>True if fragments were combined, false if not enough fragments.</returns>
        public static bool TryCombine(Player player, string biome)
        {
            if (player == null || string.IsNullOrEmpty(biome))
                return false;

            var inventory = player.GetInventory();
            if (inventory == null)
                return false;

            // Find all fragment stacks matching this biome and count total quantity
            var matchingItems = FindFragmentsByBiome(inventory, biome);
            var totalCount = 0;
            foreach (var item in matchingItems)
                totalCount += item.m_stack;

            if (totalCount < RequiredFragments)
            {
                var needed = RequiredFragments - totalCount;
                player.Message(MessageHud.MessageType.Center,
                    $"Need {needed} more {biome} fragments ({totalCount}/{RequiredFragments})");
                return false;
            }

            // Determine NPC type for this biome
            if (!BiomeNpcMap.TryGetValue(biome, out var villagerType))
            {
                Plugin.Log?.LogWarning($"No NPC type mapped for biome: {biome}");
                villagerType = "Farmer";
            }

            // Find a location in the target biome BEFORE consuming fragments.
            // If no valid location exists in the loaded world, fail fast and leave
            // the fragments in the player's inventory so they can retry elsewhere.
            // The pawn is NOT spawned now — it will be spawned by RescueQuestTracker
            // when the player arrives at the quest location (zone must be loaded).
            var questPos = FindPositionInBiome(biome, player.transform.position);
            if (!questPos.HasValue)
            {
                Plugin.Log?.LogError(
                    $"Cannot combine {biome} fragments: no valid {biome} location found " +
                    $"in loaded world (player at {player.transform.position}, " +
                    $"villagerType={villagerType}, fragmentsHeld={totalCount}). " +
                    "Fragments NOT consumed.");
                player.Message(MessageHud.MessageType.Center,
                    $"Cannot locate {biome} in the surrounding world. Try again from a different area.");
                return false;
            }

            // Consume 3 fragments across stacks now that we know the quest can be placed.
            var toRemove = RequiredFragments;
            foreach (var item in matchingItems)
            {
                if (toRemove <= 0) break;

                var removeFromStack = Mathf.Min(toRemove, item.m_stack);
                for (var i = 0; i < removeFromStack; i++)
                    inventory.RemoveOneItem(item);
                toRemove -= removeFromStack;
            }

            AddQuestMarker(questPos.Value, villagerType, biome);
            RescueQuestTracker.AddQuest(questPos.Value, villagerType, biome);
            player.Message(MessageHud.MessageType.Center,
                $"Rescue quest revealed! A captive {villagerType} awaits in the {biome}.");

            Plugin.Log?.LogInfo($"Combined {RequiredFragments} {biome} fragments -> rescue quest for {villagerType}");
            return true;
        }

        /// <summary>
        ///     Gets the biome string from a fragment item definition name.
        /// </summary>
        public static string GetBiomeFromItem(string itemName)
        {
            var def = ItemFactory.GetDefinition(itemName);
            if (def != null && def.itemType == "fragment" && !string.IsNullOrEmpty(def.biome))
                return def.biome;
            return null;
        }

        private static List<ItemDrop.ItemData> FindFragmentsByBiome(Inventory inventory, string biome)
        {
            var results = new List<ItemDrop.ItemData>();
            var allItems = inventory.GetAllItems();

            foreach (var item in allItems)
            {
                var prefabName = item?.m_dropPrefab?.name;
                if (string.IsNullOrEmpty(prefabName))
                    continue;

                var def = ItemFactory.GetDefinition(prefabName);
                if (def != null && def.itemType == "fragment" && def.biome == biome)
                    results.Add(item);
            }

            return results;
        }

        /// <summary>
        ///     Finds an existing dungeon/location in the target biome from ZoneSystem's
        ///     placed location instances. This guarantees the quest spawns at a real location
        ///     that is in the correct biome with appropriate enemies.
        ///     Picks a random matching location, preferring ones closer to the player.
        /// </summary>
        private static Vector3? FindPositionInBiome(string biomeName, Vector3 playerPos)
        {
            if (!BiomeLocationMap.TryGetValue(biomeName, out var locationNames))
            {
                Plugin.Log?.LogWarning($"No location names mapped for biome: {biomeName}");
                return null;
            }

            if (ZoneSystem.instance == null)
                return null;

            var locationSet = new HashSet<string>(locationNames);
            var candidates = new List<Vector3>();

            // Search all placed location instances for matching dungeon/location names
            foreach (var kvp in ZoneSystem.instance.m_locationInstances)
            {
                var locInstance = kvp.Value;
                if (locInstance.m_location == null)
                    continue;

                var locName = locInstance.m_location.m_prefabName;
                if (string.IsNullOrEmpty(locName))
                    continue;

                if (locationSet.Contains(locName))
                    candidates.Add(locInstance.m_position);
            }

            if (candidates.Count == 0)
            {
                Plugin.Log?.LogWarning(
                    $"No placed locations found for biome {biomeName} " +
                    $"(searched for: {string.Join(", ", locationNames)})");
                return null;
            }

            // Sort by distance to player and pick from the closest ~25%
            // to avoid sending players across the entire map
            candidates.Sort((a, b) =>
                Vector3.Distance(playerPos, a).CompareTo(Vector3.Distance(playerPos, b)));

            var poolSize = Mathf.Max(1, candidates.Count / 4);
            var chosenIndex = Random.Range(0, poolSize);
            var chosen = candidates[chosenIndex];

            Plugin.Log?.LogInfo(
                $"Selected location for {biomeName} rescue quest: {chosen} " +
                $"(distance: {Vector3.Distance(playerPos, chosen):F0}m, " +
                $"{candidates.Count} candidates found, picked from top {poolSize})");

            return chosen;
        }

        private static void AddQuestMarker(Vector3 position, string villagerType, string biome)
        {
            var minimap = Minimap.instance;
            if (minimap == null)
            {
                Plugin.Log?.LogWarning("Cannot add quest marker: Minimap not available");
                return;
            }

            var pinName = $"Rescue: Captive {villagerType} ({biome})";

            try
            {
                // Minimap.AddPin signature (from IL):
                //   PinData AddPin(Vector3 pos, PinType type, string name,
                //                  bool save, bool isChecked,
                //                  [opt] long ownerID, [opt] PlatformUserID author)
                // Find by name since the PlatformUserID type is in a separate assembly
                var addPinMethods = typeof(Minimap).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => m.Name == "AddPin")
                    .ToArray();

                MethodInfo addPinMethod = null;
                foreach (var method in addPinMethods)
                {
                    var parms = method.GetParameters();
                    if (parms.Length >= 5 &&
                        parms[0].ParameterType == typeof(Vector3) &&
                        parms[2].ParameterType == typeof(string))
                    {
                        addPinMethod = method;
                        break;
                    }
                }

                if (addPinMethod != null)
                {
                    var parms = addPinMethod.GetParameters();
                    // Build args array matching all parameters, using defaults for optional ones
                    var args = new object[parms.Length];
                    args[0] = position;
                    args[1] = Minimap.PinType.Icon3;
                    args[2] = pinName;
                    args[3] = true; // save
                    args[4] = false; // isChecked
                    // Fill optional params with their defaults
                    for (var i = 5; i < parms.Length; i++)
                        if (parms[i].HasDefaultValue)
                            args[i] = parms[i].DefaultValue;
                        else if (parms[i].ParameterType.IsValueType)
                            args[i] = Activator.CreateInstance(parms[i].ParameterType);
                        else
                            args[i] = null;

                    addPinMethod.Invoke(minimap, args);
                    Plugin.Log?.LogInfo($"Added quest marker at {position}: {pinName}");
                }
                else
                {
                    Plugin.Log?.LogWarning(
                        $"Could not find Minimap.AddPin method (found {addPinMethods.Length} overloads)");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to add quest marker: {ex.Message}");
            }
        }
    }
}