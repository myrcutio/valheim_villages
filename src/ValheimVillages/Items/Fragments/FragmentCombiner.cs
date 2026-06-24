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
            { "BlackForest", "Carpenter" },
            { "Swamp", "Blacksmith" },
            { "Mountain", "Mountaineer" },
            { "Plains", "Farmer" },
            { "Mistlands", "Guard" },
            { "Ashlands", "Guard" },
        };

        /// <summary>
        ///     The biome map(s) a player must complete to learn to recruit a given villager
        ///     type — the reverse of <see cref="BiomeNpcMap" />. A type can be taught by more
        ///     than one biome (e.g. Farmer from Meadows or Plains).
        /// </summary>
        public static IEnumerable<string> BiomesForType(string villagerType)
        {
            return BiomeNpcMap.Where(kv => kv.Value == villagerType).Select(kv => kv.Key);
        }

        /// <summary>
        ///     Maps biome identifiers to dungeon/location prefab names that can host rescue quests.
        ///     These are locations that are guaranteed to spawn in the correct biome and have
        ///     appropriate enemy difficulty. Preferred locations are listed first.
        /// </summary>
        private static readonly Dictionary<string, string[]> BiomeLocationMap = new()
        {
            { "Meadows", new[] { "WoodFarm1", "StoneCircle", "Dolmen01", "Dolmen02", "Dolmen03", "ShipSetting01", "WoodVillage1" } },
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
        ///
        ///     The location lookup is server-authoritative: <c>ZoneSystem.m_locationInstances</c>
        ///     is populated only on the host (world save load / location generation), so a client
        ///     connected to a dedicated server sees an empty dictionary and could never find a
        ///     quest site. We therefore route the lookup to the host via
        ///     <see cref="FragmentQuestRpc" /> and finish the combine in <see cref="CompleteQuest" />
        ///     once the host replies with a real location. On a listen-host / singleplayer the RPC
        ///     resolves in-process, so this path is correct in every topology. Fragments are NOT
        ///     consumed until the host confirms a location, leaving the player free to retry on
        ///     failure.
        /// </summary>
        /// <returns>True if a location request was sent, false if there aren't enough fragments.</returns>
        public static bool TryCombine(Player player, string biome)
        {
            if (player == null || string.IsNullOrEmpty(biome))
                return false;

            var inventory = player.GetInventory();
            if (inventory == null)
                return false;

            // Count matching fragments up front so we can reject early with a helpful message
            // and avoid a pointless server round-trip when the player can't combine anyway.
            var totalCount = CountStacks(FindFragmentsByBiome(inventory, biome));
            if (totalCount < RequiredFragments)
            {
                var needed = RequiredFragments - totalCount;
                player.Message(MessageHud.MessageType.Center,
                    $"Need {needed} more {biome} fragments ({totalCount}/{RequiredFragments})");
                return false;
            }

            // Ask the host to resolve a real location in this biome (the only server-authoritative
            // step). The result handler (OnResult -> CompleteQuest) consumes the fragments and
            // places the quest once the host replies.
            FragmentQuestRpc.RequestQuestLocation(biome);
            return true;
        }

        /// <summary>
        ///     Finishes a combine on the requesting client after the host has resolved a real
        ///     <paramref name="questPos" /> for <paramref name="biome" />: consumes 3 fragments,
        ///     drops the map pin, registers the pending rescue quest, and unlocks the biome's
        ///     recruit recipe for this player. Re-validates the fragment count first, since the
        ///     inventory could have changed during the (usually instant) server round-trip.
        /// </summary>
        internal static void CompleteQuest(Player player, string biome, Vector3 questPos)
        {
            if (player == null || string.IsNullOrEmpty(biome))
                return;

            var inventory = player.GetInventory();
            if (inventory == null)
                return;

            var matchingItems = FindFragmentsByBiome(inventory, biome);
            var totalCount = CountStacks(matchingItems);
            if (totalCount < RequiredFragments)
            {
                // Fragments were dropped/used while the host was resolving the location — bail
                // without consuming anything so the player keeps what's left.
                var needed = RequiredFragments - totalCount;
                player.Message(MessageHud.MessageType.Center,
                    $"Need {needed} more {biome} fragments ({totalCount}/{RequiredFragments})");
                return;
            }

            // Determine NPC type for this biome
            if (!BiomeNpcMap.TryGetValue(biome, out var villagerType))
            {
                Plugin.Log?.LogWarning($"No NPC type mapped for biome: {biome}");
                villagerType = "Farmer";
            }

            // Consume 3 fragments across stacks now that the host has confirmed a location.
            var toRemove = RequiredFragments;
            foreach (var item in matchingItems)
            {
                if (toRemove <= 0) break;

                var removeFromStack = Mathf.Min(toRemove, item.m_stack);
                for (var i = 0; i < removeFromStack; i++)
                    inventory.RemoveOneItem(item);
                toRemove -= removeFromStack;
            }

            AddQuestMarker(questPos, villagerType, biome);
            RescueQuestTracker.AddQuest(questPos, villagerType, biome);

            // Completing the map still teaches this player to recruit the biome's villager type
            // (per-player unlock) — but quietly. Keep the on-screen text terse and cryptic; the
            // map pin (Valheim's native discovery) is what actually points the player there.
            var newlyLearned = global::ValheimVillages.Villager.RecruitUnlocks.Unlock(player, villagerType);
            player.Message(MessageHud.MessageType.Center, "Quest location discovered");

            Plugin.Log?.LogInfo(
                $"Combined {RequiredFragments} {biome} fragments -> rescue quest for {villagerType} " +
                $"at {questPos} (recruit recipe {(newlyLearned ? "UNLOCKED" : "already known")})");
        }

        /// <summary>
        ///     Called on the requesting client when the host could not resolve a location for
        ///     <paramref name="biome" />. Fragments are left untouched so the player can retry.
        /// </summary>
        internal static void OnQuestLocationUnavailable(Player player, string biome, string reason)
        {
            Plugin.Log?.LogError(
                $"Cannot combine {biome} fragments: {reason}. Fragments NOT consumed.");
            player?.Message(MessageHud.MessageType.Center,
                $"Cannot locate {biome} in the surrounding world. Try again from a different area.");
        }

        /// <summary>
        ///     Sums stack sizes across the given fragment item stacks.
        /// </summary>
        private static int CountStacks(List<ItemDrop.ItemData> items)
        {
            var total = 0;
            foreach (var item in items)
                total += item.m_stack;
            return total;
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
        ///
        ///     HOST-ONLY: reads <c>ZoneSystem.m_locationInstances</c>, which is populated only
        ///     on the server. Invoked from <see cref="FragmentQuestRpc" />'s server handler, never
        ///     directly from the client.
        /// </summary>
        internal static Vector3? FindPositionInBiome(string biomeName, Vector3 playerPos)
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

            var pinName = $"Lost {villagerType} ({biome})";

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