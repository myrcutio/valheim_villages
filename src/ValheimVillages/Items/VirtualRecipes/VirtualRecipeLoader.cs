using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ValheimVillages.NPCs.AI.Work;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    /// Loads virtual recipe definitions from embedded JSON and registers them
    /// as real Recipe objects in ObjectDB. Creates hidden CraftingStation
    /// template GameObjects for recipe station references.
    /// </summary>
    public static class VirtualRecipeLoader
    {
        private static readonly List<Recipe> _registeredRecipes = new();
        private static readonly Dictionary<string, CraftingStation> _stationTemplates = new();
        private static readonly Dictionary<string, string> _physicalStationMap = new();

        /// <summary>Output item names to exclude from the villager craft/orders menu (e.g. AncientSeed).</summary>
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

            // Avoid duplicate registration when our static list still has refs (same session)
            if (_registeredRecipes.Count > 0)
            {
                ReAddExisting(objectDB);
                return;
            }

            // Hot reload resets statics, so ObjectDB may still have our recipes. Remove them first
            // so adding is idempotent (no duplicates across reloads).
            RemoveOurRecipesFrom(objectDB);

            var files = LoadDefinitions();
            int count = 0;

            foreach (var file in files)
            {
                if (string.IsNullOrEmpty(file.station) || file.station == "$vv_unknown")
                    continue;

                var station = GetOrCreateStationTemplate(file.station);
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

                // Farmer: add planting recipes from the cultivator (crops + mod plants)
                if (file.station == "$vv_farmer")
                {
                    var exclusions = GetCultivatorExclusionsLower(file);
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
                    // Farmer: add cooking recipes from the CookingStation (vanilla + mod cookable items)
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
        /// When ZNetScene becomes available (after ObjectDB), add cooking-discovered recipes
        /// for the farmer so mod cookable items are included. No-op if already added or ObjectDB not ready.
        /// </summary>
        public static void RegisterCookingRecipesIfNeeded(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null || _registeredRecipes.Count == 0) return;
            var station = GetOrCreateStationTemplate("$vv_farmer");
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
        /// Re-runs cultivator and cooking discovery and adds any new recipes (e.g. from mods
        /// that loaded after us). Call once after world load via a low-priority queue task.
        /// Returns the number of new recipes added.
        /// </summary>
        public static int RecheckDiscoveredRecipes(ObjectDB objectDB)
        {
            if (objectDB?.m_recipes == null) return 0;
            var station = GetOrCreateStationTemplate("$vv_farmer");
            if (station == null) return 0;

            var existingOutputs = new HashSet<string>();
            foreach (var r in _registeredRecipes)
            {
                if (r?.m_item?.gameObject == null || r.name == null || !r.name.StartsWith("VV_Recipe_$vv_farmer_"))
                    continue;
                existingOutputs.Add(r.m_item.gameObject.name);
            }

            var files = LoadDefinitions();
            VirtualRecipeFile farmerFile = null;
            foreach (var f in files)
            {
                if (f?.station == "$vv_farmer") { farmerFile = f; break; }
            }
            var exclusions = farmerFile != null ? GetCultivatorExclusionsLower(farmerFile) : System.Array.Empty<string>();

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
        /// Used by VillagerStation to reference the same station for recipe matching.
        /// </summary>
        public static CraftingStation GetStationTemplate(string stationName)
        {
            return _stationTemplates.TryGetValue(stationName, out var s) ? s : null;
        }

        /// <summary>
        /// Get the physical station type override for a recipe, or null if none.
        /// E.g. returns "cookingstation" for cooking recipes that should route
        /// to a CookingStation instead of the virtual station.
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

        /// <summary>
        /// Remove any VV virtual recipes already in ObjectDB (e.g. from a previous hot reload).
        /// Makes RegisterAll idempotent when statics are reset.
        /// </summary>
        private static void RemoveOurRecipesFrom(ObjectDB objectDB)
        {
            int removed = objectDB.m_recipes.RemoveAll(r =>
                r != null && !string.IsNullOrEmpty(r.name) && r.name.StartsWith("VV_Recipe_"));
            if (removed > 0)
                Plugin.Log?.LogInfo(
                    $"VirtualRecipeLoader: Removed {removed} existing virtual recipes before re-register");
        }

        private static List<VirtualRecipeFile> LoadDefinitions()
        {
            var results = new List<VirtualRecipeFile>();
            var assembly = Assembly.GetExecutingAssembly();

            foreach (var resourceName in assembly.GetManifestResourceNames())
            {
                if (!resourceName.Contains(".VirtualRecipes.") ||
                    !resourceName.EndsWith(".json"))
                    continue;

                try
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    using var reader = new StreamReader(stream);
                    var json = reader.ReadToEnd();
                    var file = JsonUtility.FromJson<VirtualRecipeFile>(json);
                    if (file != null)
                    {
                        // Unity JsonUtility often leaves nested arrays (e.g. recipes[].inputs) null;
                        // if recipes is null, re-parse the raw "recipes":[...] segment.
                        if (string.IsNullOrEmpty(file.station))
                            file.station = "$vv_unknown";
                        if (file.recipes == null || file.recipes.Length == 0)
                            TryParseRecipesArray(json, file);
                        if (file.cultivatorPieceExclusions == null)
                            TryParseCultivatorExclusions(json, file);
                        results.Add(file);
                    }
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogError(
                        $"VirtualRecipeLoader: Failed to load {resourceName}: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Fallback when JsonUtility leaves file.recipes null (nested arrays).
        /// Extracts the "recipes":[...] segment and parses each recipe object individually.
        /// </summary>
        private static void TryParseRecipesArray(string json, VirtualRecipeFile file)
        {
            var idx = json.IndexOf("\"recipes\"", System.StringComparison.Ordinal);
            if (idx < 0) return;
            var start = json.IndexOf('[', idx);
            if (start < 0) return;
            int depth = 1, i = start + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') depth--;
                i++;
            }
            if (depth != 0) return;
            var arrayBody = json.Substring(start + 1, i - start - 2); // content between [ and ]
            var entries = new List<VirtualRecipeEntry>();
            var parts = SplitRecipeObjects(arrayBody);
            foreach (var part in parts)
            {
                var entry = JsonUtility.FromJson<VirtualRecipeEntry>(part);
                if (entry == null || string.IsNullOrEmpty(entry.output))
                {
                    var minimal = JsonUtility.FromJson<VirtualRecipeEntryMinimal>(part);
                    if (minimal != null && !string.IsNullOrEmpty(minimal.output))
                        entry = new VirtualRecipeEntry { output = minimal.output, outputAmount = minimal.outputAmount, minStationLevel = minimal.minStationLevel, physicalStation = minimal.physicalStation, inputs = null };
                }
                if (entry != null && !string.IsNullOrEmpty(entry.output))
                {
                    if (entry.inputs == null || entry.inputs.Length == 0)
                        entry.inputs = TryParseInputsFromRecipeJson(part);
                    entries.Add(entry);
                }
            }
            if (entries.Count > 0)
                file.recipes = entries.ToArray();
        }

        /// <summary>
        /// Fallback when JsonUtility leaves file.cultivatorPieceExclusions null.
        /// Extracts the "cultivatorPieceExclusions":["a","b",...] array.
        /// </summary>
        private static void TryParseCultivatorExclusions(string json, VirtualRecipeFile file)
        {
            var idx = json.IndexOf("\"cultivatorPieceExclusions\"", System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return;
            var start = json.IndexOf('[', idx);
            if (start < 0) return;
            int depth = 1, i = start + 1;
            while (i < json.Length && depth > 0)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') depth--;
                i++;
            }
            if (depth != 0) return;
            var arrayBody = json.Substring(start + 1, i - start - 2);
            var list = new List<string>();
            for (int j = 0; j < arrayBody.Length; j++)
            {
                if (arrayBody[j] != '"') continue;
                var end = j + 1;
                while (end < arrayBody.Length && (arrayBody[end] != '"' || (end > 0 && arrayBody[end - 1] == '\\')))
                    end++;
                if (end >= arrayBody.Length) break;
                var s = arrayBody.Substring(j + 1, end - j - 1).Replace("\\\"", "\"").Trim();
                if (!string.IsNullOrEmpty(s))
                    list.Add(s.ToLowerInvariant());
                j = end;
            }
            if (list.Count > 0)
                file.cultivatorPieceExclusions = list.ToArray();
        }

        /// <summary>
        /// Returns a single list of lowercased exclusion substrings for cultivator discovery.
        /// Built once per file at load time; empty if cultivatorPieceExclusions is null/empty.
        /// </summary>
        private static IReadOnlyList<string> GetCultivatorExclusionsLower(VirtualRecipeFile file)
        {
            if (file?.cultivatorPieceExclusions == null || file.cultivatorPieceExclusions.Length == 0)
                return System.Array.Empty<string>();
            var lower = new string[file.cultivatorPieceExclusions.Length];
            for (int i = 0; i < file.cultivatorPieceExclusions.Length; i++)
            {
                var s = file.cultivatorPieceExclusions[i];
                lower[i] = string.IsNullOrEmpty(s) ? "" : s.Trim().ToLowerInvariant();
            }
            return lower;
        }

        /// <summary>
        /// Parse the "inputs":[...] array from a single recipe JSON string.
        /// Unity often fails to deserialize arrays of objects, so we extract the array body,
        /// split into individual {"item":"X","amount":N} chunks, and parse each with JsonUtility.
        /// </summary>
        private static VirtualRecipeInput[] TryParseInputsFromRecipeJson(string recipeJson)
        {
            if (string.IsNullOrEmpty(recipeJson)) return null;
            var idx = recipeJson.IndexOf("\"inputs\"", System.StringComparison.Ordinal);
            if (idx < 0) return null;
            var start = recipeJson.IndexOf('[', idx);
            if (start < 0) return null;
            int depth = 1, i = start + 1;
            while (i < recipeJson.Length && depth > 0)
            {
                if (recipeJson[i] == '[') depth++;
                else if (recipeJson[i] == ']') depth--;
                i++;
            }
            if (depth != 0) return null;
            var arrayBody = recipeJson.Substring(start + 1, i - start - 2); // content between [ and ]
            var parts = SplitRecipeObjects(arrayBody);
            if (parts == null || parts.Count == 0) return null;
            var list = new List<VirtualRecipeInput>();
            foreach (var part in parts)
            {
                var input = JsonUtility.FromJson<VirtualRecipeInput>(part);
                if (input != null && !string.IsNullOrEmpty(input.item))
                    list.Add(input);
            }
            return list.Count > 0 ? list.ToArray() : null;
        }

        /// <summary>Split "[{...},{...}]" body by object boundaries so each chunk is one {...}.</summary>
        private static List<string> SplitRecipeObjects(string arrayBody)
        {
            var list = new List<string>();
            int depth = 0, start = -1;
            for (int i = 0; i < arrayBody.Length; i++)
            {
                if (arrayBody[i] == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (arrayBody[i] == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(arrayBody.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }
            return list;
        }

        private static CraftingStation GetOrCreateStationTemplate(string stationName)
        {
            if (_stationTemplates.TryGetValue(stationName, out var existing))
                return existing;

            // Create a hidden GameObject with a CraftingStation component.
            // This is never placed in the world -- it exists only so Recipe
            // objects can reference it for station-name matching.
            var go = new GameObject($"VV_StationTemplate_{stationName}");
            go.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(go);

            var station = go.AddComponent<CraftingStation>();
            station.m_name = stationName;
            station.m_icon = null;
            station.m_discoverRange = 0f;
            station.m_rangeBuild = 0f;
            station.m_craftRequireRoof = false;
            station.m_craftRequireFire = false;
            station.m_showBasicRecipies = false;
            station.m_useDistance = 10f;
            station.m_useAnimation = 0;
            station.m_areaMarker = null;
            station.m_inUseObject = null;
            station.m_haveFireObject = null;
            station.m_craftItemEffects = new EffectList();
            station.m_craftItemDoneEffects = new EffectList();
            station.m_repairItemDoneEffects = new EffectList();

            _stationTemplates[stationName] = station;
            Plugin.Log?.LogInfo(
                $"VirtualRecipeLoader: Created station template for '{stationName}'");

            return station;
        }

        private static Recipe CreateRecipe(
            ObjectDB objectDB, CraftingStation station, VirtualRecipeEntry entry)
        {
            if (string.IsNullOrEmpty(entry.output))
            {
                Plugin.Log?.LogWarning("VirtualRecipeLoader: Recipe has no output");
                return null;
            }

            // Find the output item prefab
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

            // Build resource requirements
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

            // Create the Recipe ScriptableObject
            var recipeName = $"VV_Recipe_{station.m_name}_{entry.output}";
            var recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = recipeName;
            recipe.m_item = outputItemDrop;
            recipe.m_amount = entry.outputAmount;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = entry.minStationLevel;
            recipe.m_resources = resources.ToArray();
            recipe.m_enabled = true;

            // Record physical station override if specified
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
