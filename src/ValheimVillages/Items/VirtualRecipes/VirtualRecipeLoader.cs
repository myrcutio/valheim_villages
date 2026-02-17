using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.NPCs.AI.Work;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    /// Registers virtual recipe definitions as real Recipe objects in ObjectDB.
    /// JSON parsing and station templates are delegated to <see cref="VirtualRecipeParser"/>.
    /// </summary>
    public static class VirtualRecipeLoader
    {
        private static readonly List<Recipe> _registeredRecipes = new();
        private static readonly Dictionary<string, string> _physicalStationMap = new();

        /// <summary>Output item names to exclude from the villager craft/orders menu.</summary>
        private static bool IsExcludedFromCraftMenu(string output)
        {
            return string.Equals(output, "AncientSeed", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Register all virtual recipes with ObjectDB.
        /// Called from ObjectDB.Awake / CopyOtherDB patches.
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

            var files = VirtualRecipeParser.LoadDefinitions();
            int count = 0;

            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file.station) || file.station == "$vv_unknown")
                    continue;

                var station = VirtualRecipeParser.GetOrCreateStationTemplate(file.station);
                var existingOutputs = new HashSet<string>();

                if (file.recipes != null)
                {
                    foreach (var entry in file.recipes)
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
                }

                // Farmer: add planting recipes from the cultivator
                if (file.station == "$vv_farmer")
                {
                    var exclusions = VirtualRecipeParser.GetCultivatorExclusionsLower(file);
                    var cultivatorEntries = CultivatorRecipeDiscovery.GetPlantingRecipes(existingOutputs, exclusions);
                    foreach (var entry in cultivatorEntries)
                    {
                        if (IsExcludedFromCraftMenu(entry.output)) continue;
                        if (!string.IsNullOrEmpty(entry.output)) existingOutputs.Add(entry.output);
                        var recipe = CreateRecipe(objectDB, station, entry);
                        if (recipe != null)
                        {
                            objectDB.m_recipes.Add(recipe);
                            _registeredRecipes.Add(recipe);
                            count++;
                        }
                    }
                    // Farmer: add cooking recipes
                    var cookingEntries = CookingRecipeDiscovery.GetCookingRecipes(existingOutputs);
                    foreach (var entry in cookingEntries)
                    {
                        var recipe = CreateRecipe(objectDB, station, entry);
                        if (recipe != null)
                        {
                            objectDB.m_recipes.Add(recipe);
                            _registeredRecipes.Add(recipe);
                            count++;
                        }
                    }
                }
            }

            Plugin.Log?.LogInfo(
                $"VirtualRecipeLoader: Registered {count} virtual recipes");
        }

        /// <summary>
        /// When ZNetScene becomes available, add cooking-discovered recipes for the farmer.
        /// </summary>
        public static void RegisterCookingRecipesIfNeeded(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null || _registeredRecipes.Count == 0) return;
            var station = VirtualRecipeParser.GetOrCreateStationTemplate("$vv_farmer");
            if (station == null) return;
            var existingOutputs = new HashSet<string>();
            foreach (var r in _registeredRecipes)
            {
                if (r?.m_item?.gameObject == null || r.name == null || !r.name.StartsWith("VV_Recipe_$vv_farmer_"))
                    continue;
                existingOutputs.Add(r.m_item.gameObject.name);
            }
            var cookingEntries = CookingRecipeDiscovery.GetCookingRecipes(existingOutputs);
            if (cookingEntries.Count == 0) return;
            foreach (var entry in cookingEntries)
            {
                var recipe = CreateRecipe(objectDB, station, entry);
                if (recipe != null)
                {
                    objectDB.m_recipes.Add(recipe);
                    _registeredRecipes.Add(recipe);
                }
            }
            Plugin.Log?.LogInfo(
                $"VirtualRecipeLoader: Registered {cookingEntries.Count} cooking-discovered recipes (ZNetScene ready)");
        }

        /// <summary>
        /// Re-runs cultivator and cooking discovery and adds any new recipes.
        /// Returns the number of new recipes added.
        /// </summary>
        public static int RecheckDiscoveredRecipes(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null) return 0;
            var station = VirtualRecipeParser.GetOrCreateStationTemplate("$vv_farmer");
            if (station == null) return 0;

            var existingOutputs = new HashSet<string>();
            foreach (var r in _registeredRecipes)
            {
                if (r?.m_item?.gameObject == null || r.name == null || !r.name.StartsWith("VV_Recipe_$vv_farmer_"))
                    continue;
                existingOutputs.Add(r.m_item.gameObject.name);
            }

            var files = VirtualRecipeParser.LoadDefinitions();
            VirtualRecipeFile farmerFile = null;
            foreach (var f in files)
            {
                if (f?.station == "$vv_farmer") { farmerFile = f; break; }
            }
            var exclusions = farmerFile != null ? VirtualRecipeParser.GetCultivatorExclusionsLower(farmerFile) : Array.Empty<string>();

            int added = 0;
            var cultivatorEntries = CultivatorRecipeDiscovery.GetPlantingRecipes(existingOutputs, exclusions);
            foreach (var entry in cultivatorEntries)
            {
                if (!string.IsNullOrEmpty(entry.output)) existingOutputs.Add(entry.output);
                var recipe = CreateRecipe(objectDB, station, entry);
                if (recipe != null)
                {
                    objectDB.m_recipes.Add(recipe);
                    _registeredRecipes.Add(recipe);
                    added++;
                }
            }
            var cookingEntries = CookingRecipeDiscovery.GetCookingRecipes(existingOutputs);
            foreach (var entry in cookingEntries)
            {
                var recipe = CreateRecipe(objectDB, station, entry);
                if (recipe != null)
                {
                    objectDB.m_recipes.Add(recipe);
                    _registeredRecipes.Add(recipe);
                    added++;
                }
            }
            if (added > 0)
                Plugin.Log?.LogInfo($"VirtualRecipeLoader: Recheck added {added} discovered recipes (post–world load)");
            return added;
        }

        /// <summary>
        /// Get the template CraftingStation for a virtual station name.
        /// </summary>
        public static CraftingStation GetStationTemplate(string stationName)
        {
            return VirtualRecipeParser.GetStationTemplate(stationName);
        }

        /// <summary>
        /// Get the physical station type override for a recipe, or null if none.
        /// </summary>
        public static string GetPhysicalStation(string recipeName)
        {
            if (string.IsNullOrEmpty(recipeName)) return null;
            return _physicalStationMap.TryGetValue(recipeName, out var ps) ? ps : null;
        }

        private static void ReAddExisting(ObjectDB objectDB)
        {
            int added = 0;
            foreach (var recipe in _registeredRecipes)
            {
                if (!objectDB.m_recipes.Contains(recipe))
                {
                    objectDB.m_recipes.Add(recipe);
                    added++;
                }
            }

            if (added > 0)
                Plugin.Log?.LogInfo(
                    $"VirtualRecipeLoader: Re-added {added} virtual recipes");
        }

        private static void RemoveOurRecipesFrom(ObjectDB objectDB)
        {
            int removed = objectDB.m_recipes.RemoveAll(r =>
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
            {
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
                        m_recover = false
                    });
                }
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
