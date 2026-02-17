using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    /// JSON parsing, definition loading, and station template management for virtual recipes.
    /// Extracted from VirtualRecipeLoader to separate parsing concerns from recipe registration.
    /// </summary>
    internal static class VirtualRecipeParser
    {
        private static readonly Dictionary<string, CraftingStation> _stationTemplates = new();

        internal static List<VirtualRecipeFile> LoadDefinitions()
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
                        $"VirtualRecipeParser: Failed to load {resourceName}: {ex.Message}");
                }
            }

            return results;
        }

        /// <summary>
        /// Fallback when JsonUtility leaves file.recipes null (nested arrays).
        /// </summary>
        internal static void TryParseRecipesArray(string json, VirtualRecipeFile file)
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
            var arrayBody = json.Substring(start + 1, i - start - 2);
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
        /// </summary>
        internal static void TryParseCultivatorExclusions(string json, VirtualRecipeFile file)
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
        /// </summary>
        internal static IReadOnlyList<string> GetCultivatorExclusionsLower(VirtualRecipeFile file)
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
        /// </summary>
        internal static VirtualRecipeInput[] TryParseInputsFromRecipeJson(string recipeJson)
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
            var arrayBody = recipeJson.Substring(start + 1, i - start - 2);
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
        internal static List<string> SplitRecipeObjects(string arrayBody)
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

        internal static CraftingStation GetOrCreateStationTemplate(string stationName)
        {
            if (_stationTemplates.TryGetValue(stationName, out var existing))
                return existing;

            var go = new GameObject($"VV_StationTemplate_{stationName}");
            go.SetActive(false);
            Object.DontDestroyOnLoad(go);

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
                $"VirtualRecipeParser: Created station template for '{stationName}'");

            return station;
        }

        /// <summary>
        /// Get the template CraftingStation for a virtual station name.
        /// </summary>
        internal static CraftingStation GetStationTemplate(string stationName)
        {
            return _stationTemplates.TryGetValue(stationName, out var s) ? s : null;
        }
    }
}
