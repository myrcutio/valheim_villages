using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.Tags;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    ///     Registers virtual recipe definitions as real Recipe objects in ObjectDB.
    ///     Reads station recipes and discovery tags from VillagerRegistry definitions
    ///     instead of separate JSON files.
    /// </summary>
    public static class VirtualRecipeLoader
    {
        private static readonly List<Recipe> _registeredRecipes = new();
        private static readonly Dictionary<string, string> _physicalStationMap = new();

        /// <summary>
        ///     Clear cached recipe state on world unload / hot reload so the
        ///     next <see cref="RegisterAll" /> performs a full re-discovery
        ///     instead of short-circuiting via <see cref="ReAddExisting" />.
        ///     Without this, duplicates accumulated across reloads stick
        ///     around until Valheim restarts entirely — the loader thinks
        ///     it already has the recipes and re-adds them as-is.
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            _registeredRecipes.Clear();
            _physicalStationMap.Clear();
        }

        private static bool IsExcludedFromCraftMenu(string output)
        {
            return string.Equals(output, "AncientSeed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        ///     Register all virtual recipes with ObjectDB.
        ///     Iterates VillagerRegistry definitions for station recipes and
        ///     uses recipe:cultivator / recipe:cooking tags for discovery.
        /// </summary>
        public static void RegisterAll(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null)
            {
                Plugin.Log?.LogWarning("VirtualRecipeLoader: ObjectDB not ready");
                return;
            }

            if (_registeredRecipes.Count > 0)
            {
                ReAddExisting(objectDB);
                return;
            }

            RemoveOurRecipesFrom(objectDB);

            var count = 0;

            foreach (var kv in VillagerRegistry.Definitions)
            {
                var def = kv.Value;
                if (string.IsNullOrEmpty(def?.stationName)) continue;

                var station = VirtualRecipeParser.GetOrCreateStationTemplate(def.stationName);
                var existingOutputs = new HashSet<string>();

                count += RegisterStationRecipes(objectDB, station, def.stationRecipes, existingOutputs);

                if (def.tags != null && TagParser.HasTag(def.tags, "recipe", "cultivator"))
                {
                    var exclusions = GetCultivatorExclusionsLower(def);
                    var cultivatorEntries = CultivatorRecipeDiscovery.GetPlantingRecipes(existingOutputs, exclusions);
                    count += RegisterDiscoveredEntries(objectDB, station, cultivatorEntries, existingOutputs);
                }

                if (def.tags != null && TagParser.HasTag(def.tags, "recipe", "cooking"))
                {
                    var cookingEntries = CookingRecipeDiscovery.GetCookingRecipes(existingOutputs);
                    count += RegisterDiscoveredEntries(objectDB, station, cookingEntries, existingOutputs);
                }

                if (def.tags != null && TagParser.HasTag(def.tags, "recipe", "smelter"))
                {
                    var smelterEntries = SmelterRecipeDiscovery.GetSmelterRecipes(existingOutputs);
                    count += RegisterDiscoveredEntries(objectDB, station, smelterEntries, existingOutputs);
                }
            }

            Plugin.Log?.LogInfo(
                $"VirtualRecipeLoader: Registered {count} virtual recipes");
        }

        /// <summary>
        ///     When ZNetScene becomes available, add cooking- AND smelter-
        ///     discovered recipes for any station with the matching
        ///     recipe:cooking or recipe:smelter tag. Both discovery sources
        ///     need ZNetScene populated with the relevant prefabs before
        ///     they can enumerate conversions.
        /// </summary>
        public static void RegisterCookingRecipesIfNeeded(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null || _registeredRecipes.Count == 0) return;

            foreach (var kv in VillagerRegistry.Definitions)
            {
                var def = kv.Value;
                if (string.IsNullOrEmpty(def?.stationName)) continue;
                if (def.tags == null) continue;

                var station = VirtualRecipeParser.GetOrCreateStationTemplate(def.stationName);
                if (station == null) continue;

                if (TagParser.HasTag(def.tags, "recipe", "cooking"))
                {
                    var existingOutputs = CollectExistingOutputs(def.stationName);
                    var cookingEntries = CookingRecipeDiscovery.GetCookingRecipes(existingOutputs);
                    if (cookingEntries.Count > 0)
                    {
                        var added = RegisterDiscoveredEntries(objectDB, station, cookingEntries, existingOutputs);
                        if (added > 0)
                            Plugin.Log?.LogInfo(
                                $"VirtualRecipeLoader: Registered {added} cooking-discovered recipes for {def.stationName} (ZNetScene ready)");
                    }
                }

                if (TagParser.HasTag(def.tags, "recipe", "smelter"))
                {
                    var existingOutputs = CollectExistingOutputs(def.stationName);
                    var smelterEntries = SmelterRecipeDiscovery.GetSmelterRecipes(existingOutputs);
                    if (smelterEntries.Count > 0)
                    {
                        var added = RegisterDiscoveredEntries(objectDB, station, smelterEntries, existingOutputs);
                        if (added > 0)
                            Plugin.Log?.LogInfo(
                                $"VirtualRecipeLoader: Registered {added} smelter-discovered recipes for {def.stationName} (ZNetScene ready)");
                    }
                }
            }
        }

        /// <summary>
        ///     Re-runs cultivator and cooking discovery and adds any new recipes.
        ///     Returns the number of new recipes added.
        /// </summary>
        public static int RecheckDiscoveredRecipes(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null) return 0;

            var totalAdded = 0;

            foreach (var kv in VillagerRegistry.Definitions)
            {
                var def = kv.Value;
                if (string.IsNullOrEmpty(def?.stationName)) continue;

                var hasCultivator = def.tags != null && TagParser.HasTag(def.tags, "recipe", "cultivator");
                var hasCooking = def.tags != null && TagParser.HasTag(def.tags, "recipe", "cooking");
                var hasSmelter = def.tags != null && TagParser.HasTag(def.tags, "recipe", "smelter");
                if (!hasCultivator && !hasCooking && !hasSmelter) continue;

                var station = VirtualRecipeParser.GetOrCreateStationTemplate(def.stationName);
                if (station == null) continue;

                var existingOutputs = CollectExistingOutputs(def.stationName);

                if (hasCultivator)
                {
                    var exclusions = GetCultivatorExclusionsLower(def);
                    var cultivatorEntries = CultivatorRecipeDiscovery.GetPlantingRecipes(existingOutputs, exclusions);
                    totalAdded += RegisterDiscoveredEntries(objectDB, station, cultivatorEntries, existingOutputs);
                }

                if (hasCooking)
                {
                    var cookingEntries = CookingRecipeDiscovery.GetCookingRecipes(existingOutputs);
                    totalAdded += RegisterDiscoveredEntries(objectDB, station, cookingEntries, existingOutputs);
                }

                if (hasSmelter)
                {
                    var smelterEntries = SmelterRecipeDiscovery.GetSmelterRecipes(existingOutputs);
                    totalAdded += RegisterDiscoveredEntries(objectDB, station, smelterEntries, existingOutputs);
                }
            }

            if (totalAdded > 0)
                Plugin.Log?.LogInfo(
                    $"VirtualRecipeLoader: Recheck added {totalAdded} discovered recipes (post-world load)");
            return totalAdded;
        }

        /// <summary>
        ///     Get the template CraftingStation for a virtual station name.
        /// </summary>
        public static CraftingStation GetStationTemplate(string stationName)
        {
            return VirtualRecipeParser.GetStationTemplate(stationName);
        }

        /// <summary>
        ///     Get the physical station type override for a recipe, or null if none.
        /// </summary>
        public static string GetPhysicalStation(string recipeName)
        {
            if (string.IsNullOrEmpty(recipeName)) return null;
            return _physicalStationMap.TryGetValue(recipeName, out var ps) ? ps : null;
        }

        private static int RegisterStationRecipes(
            ObjectDB objectDB, CraftingStation station,
            List<StationRecipe> recipes, HashSet<string> existingOutputs)
        {
            if (recipes == null || recipes.Count == 0) return 0;
            var count = 0;
            foreach (var sr in recipes)
            {
                if (IsExcludedFromCraftMenu(sr.output)) continue;
                if (!string.IsNullOrEmpty(sr.output))
                    existingOutputs.Add(sr.output);

                var entry = new VirtualRecipeEntry
                {
                    output = sr.output,
                    outputAmount = sr.outputAmount,
                    inputs = string.IsNullOrEmpty(sr.input)
                        ? null
                        : new[] { new VirtualRecipeInput { item = sr.input, amount = sr.inputAmount } },
                    minStationLevel = sr.minStationLevel,
                };

                var recipe = CreateRecipe(objectDB, station, entry);
                if (recipe != null)
                {
                    objectDB.m_recipes.Add(recipe);
                    _registeredRecipes.Add(recipe);
                    count++;
                }
            }

            return count;
        }

        private static int RegisterDiscoveredEntries(
            ObjectDB objectDB, CraftingStation station,
            List<VirtualRecipeEntry> entries, HashSet<string> existingOutputs)
        {
            var count = 0;
            foreach (var entry in entries)
            {
                if (IsExcludedFromCraftMenu(entry.output)) continue;
                if (!string.IsNullOrEmpty(entry.output))
                    existingOutputs.Add(entry.output);

                var recipe = CreateRecipe(objectDB, station, entry);
                if (recipe != null)
                {
                    objectDB.m_recipes.Add(recipe);
                    _registeredRecipes.Add(recipe);
                    count++;
                }
            }

            return count;
        }

        private static HashSet<string> CollectExistingOutputs(string stationName)
        {
            var existingOutputs = new HashSet<string>();
            var prefix = $"VV_Recipe_{stationName}_";
            foreach (var r in _registeredRecipes)
            {
                if (r?.m_item?.gameObject == null || r.name == null || !r.name.StartsWith(prefix))
                    continue;
                existingOutputs.Add(r.m_item.gameObject.name);
            }

            return existingOutputs;
        }

        private static IReadOnlyList<string> GetCultivatorExclusionsLower(VillagerDef def)
        {
            if (def?.cultivatorExclusions == null || def.cultivatorExclusions.Count == 0)
                return Array.Empty<string>();
            var lower = new string[def.cultivatorExclusions.Count];
            for (var i = 0; i < def.cultivatorExclusions.Count; i++)
            {
                var s = def.cultivatorExclusions[i];
                lower[i] = string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();
            }

            return lower;
        }

        private static void ReAddExisting(ObjectDB objectDB)
        {
            var added = 0;
            foreach (var recipe in _registeredRecipes)
                if (!objectDB.m_recipes.Contains(recipe))
                {
                    objectDB.m_recipes.Add(recipe);
                    added++;
                }

            if (added > 0)
                Plugin.Log?.LogInfo(
                    $"VirtualRecipeLoader: Re-added {added} virtual recipes");
        }

        private static void RemoveOurRecipesFrom(ObjectDB objectDB)
        {
            var removed = objectDB.m_recipes.RemoveAll(r =>
                r != null && !string.IsNullOrEmpty(r.name) && r.name.StartsWith("VV_Recipe_"));
            if (removed > 0)
                Plugin.Log?.LogInfo(
                    $"VirtualRecipeLoader: Removed {removed} existing virtual recipes before re-register");
        }

        private static Recipe CreateRecipe(
            ObjectDB objectDB, CraftingStation station, VirtualRecipeEntry entry)
        {
            if (string.IsNullOrEmpty(entry.output))
            {
                Plugin.Log?.LogWarning("VirtualRecipeLoader: Recipe has no output");
                return null;
            }

            var outputPrefab = ObjectDB.instance.GetItemPrefab(entry.output);
            if (outputPrefab == null)
            {
                Plugin.Log?.LogWarning(
                    $"VirtualRecipeLoader: Output prefab not found: {entry.output}");
                return null;
            }

            var outputItemDrop = outputPrefab.GetComponent<ItemDrop>();
            if (outputItemDrop == null)
            {
                Plugin.Log?.LogWarning(
                    $"VirtualRecipeLoader: Output prefab has no ItemDrop: {entry.output}");
                return null;
            }

            var resources = new List<Piece.Requirement>();
            if (entry.inputs != null)
                foreach (var input in entry.inputs)
                {
                    var inputPrefab = ObjectDB.instance.GetItemPrefab(input.item);
                    if (inputPrefab == null)
                    {
                        Plugin.Log?.LogWarning(
                            $"VirtualRecipeLoader: Input prefab not found: {input.item}");
                        return null;
                    }

                    var inputItemDrop = inputPrefab.GetComponent<ItemDrop>();
                    if (inputItemDrop == null)
                    {
                        Plugin.Log?.LogWarning(
                            $"VirtualRecipeLoader: Input prefab has no ItemDrop: {input.item}");
                        return null;
                    }

                    resources.Add(new Piece.Requirement
                    {
                        m_resItem = inputItemDrop,
                        m_amount = input.amount,
                        m_amountPerLevel = 0,
                        m_recover = false,
                    });
                }

            var recipeName = $"VV_Recipe_{station.m_name}_{entry.output}";
            var recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = recipeName;
            recipe.m_item = outputItemDrop;
            recipe.m_amount = entry.outputAmount;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = entry.minStationLevel;
            recipe.m_resources = resources.ToArray();
            recipe.m_enabled = true;

            if (!string.IsNullOrEmpty(entry.physicalStation))
                _physicalStationMap[recipeName] = entry.physicalStation;

            Plugin.Log?.LogInfo(
                $"VirtualRecipeLoader: Created recipe '{recipeName}' " +
                $"({entry.inputs?.Length ?? 0} inputs -> {entry.outputAmount}x {entry.output})" +
                (string.IsNullOrEmpty(entry.physicalStation) ? "" : $" [physicalStation={entry.physicalStation}]"));

            return recipe;
        }
    }
}