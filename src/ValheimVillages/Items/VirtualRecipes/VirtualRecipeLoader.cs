using System;
using HarmonyLib;
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

                // Per-definition accounting. Without it a definition whose stationRecipes
                // silently arrive empty (JSON key drift, a deserialization quirk) looks
                // identical to one that simply has none, and the missing recipes only ever
                // surface as "that work order isn't in the list".
                var fromStation = RegisterStationRecipes(objectDB, station, def.stationRecipes, existingOutputs);
                count += fromStation;
                Plugin.Log?.LogInfo(
                    $"VirtualRecipeLoader: {def.type} station='{def.stationName}' " +
                    $"stationRecipes={(def.stationRecipes == null ? "null" : def.stationRecipes.Count.ToString())} " +
                    $"registered={fromStation}");

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

                if (def.tags != null && TagParser.HasTag(def.tags, "recipe", "beekeeping"))
                {
                    var beeEntries = BeehiveRecipeDiscovery.GetBeehiveRecipes(existingOutputs);
                    count += RegisterDiscoveredEntries(objectDB, station, beeEntries, existingOutputs);
                }

                if (def.tags != null && TagParser.HasTag(def.tags, "recipe", "foraging"))
                {
                    var forageEntries = PickableRecipeDiscovery.GetForagingRecipes(
                        existingOutputs, GetForageExclusionsLower(def));
                    count += RegisterDiscoveredEntries(objectDB, station, forageEntries, existingOutputs);
                }
            }

            Plugin.Log?.LogInfo(
                $"VirtualRecipeLoader: Registered {count} virtual recipes");

            RefreshPlayerKnownRecipes();
        }

        /// <summary>
        ///     Make freshly-registered recipes actually reachable in the crafting UI.
        ///
        ///     <para>A recipe in ObjectDB is not yet a row in the player's craft list:
        ///     <c>Player.GetAvailableRecipes</c> emits only recipes present in
        ///     <c>m_knownRecipes</c>, which the game fills in <c>UpdateKnownRecipesList</c> —
        ///     and it runs that from <c>OnInventoryChanged</c>, not when ObjectDB gains
        ///     recipes. On a hot reload (and on the deferred ZNetScene-ready pass) our recipes
        ///     therefore exist but stay undiscovered until the player happens to touch their
        ///     inventory, which reads as "the mod registered it but it isn't in my list".
        ///     Nudging the game's own discovery keeps vanilla's rules (station knowledge,
        ///     materials) intact rather than force-adding entries behind its back.</para>
        /// </summary>
        private static void RefreshPlayerKnownRecipes()
        {
            var player = Player.m_localPlayer;
            if (player == null) return; // cold start: the player discovers on spawn anyway

            var method = AccessTools.Method(typeof(Player), "UpdateKnownRecipesList");
            if (method == null)
            {
                Plugin.Log?.LogWarning(
                    "[VirtualRecipeLoader] Player.UpdateKnownRecipesList not found — newly " +
                    "registered recipes will stay out of the craft list until the player's " +
                    "inventory changes.");
                return;
            }

            method.Invoke(player, null);
        }

        /// <summary>
        ///     Add cooking- AND smelter-discovered recipes for any station with the
        ///     matching recipe:cooking or recipe:smelter tag. Both discovery sources need
        ///     ObjectDB populated AND ZNetScene populated with the relevant prefabs before
        ///     they can enumerate conversions.
        ///     <para>
        ///     [RequireObjectDB]: deferred until ObjectDB is alive (which guarantees
        ///     <see cref="RegisterAll" /> ran from the ObjectDB.Awake postfix, so
        ///     <c>_registeredRecipes</c> is populated) and ZNetScene exists. Previously
        ///     called eagerly from the ZNetScene.Awake patch with <c>ObjectDB.instance</c>;
        ///     when ZNetScene.Awake won the race against ObjectDB.Awake that argument was
        ///     null / recipes weren't registered yet, so this bailed and cooking + smelter
        ///     recipes were silently lost for the whole session with no retry.
        ///     </para>
        /// </summary>
        [RequireObjectDB]
        public static void RegisterCookingRecipesIfNeeded()
        {
            var objectDB = ObjectDB.instance;
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

                if (TagParser.HasTag(def.tags, "recipe", "beekeeping"))
                {
                    var existingOutputs = CollectExistingOutputs(def.stationName);
                    var beeEntries = BeehiveRecipeDiscovery.GetBeehiveRecipes(existingOutputs);
                    if (beeEntries.Count > 0)
                    {
                        var added = RegisterDiscoveredEntries(objectDB, station, beeEntries, existingOutputs);
                        if (added > 0)
                            Plugin.Log?.LogInfo(
                                $"VirtualRecipeLoader: Registered {added} beehive-discovered recipes for {def.stationName} (ZNetScene ready)");
                    }
                }

                if (TagParser.HasTag(def.tags, "recipe", "foraging"))
                {
                    var existingOutputs = CollectExistingOutputs(def.stationName);
                    var forageEntries = PickableRecipeDiscovery.GetForagingRecipes(
                        existingOutputs, GetForageExclusionsLower(def));
                    if (forageEntries.Count > 0)
                    {
                        var added = RegisterDiscoveredEntries(objectDB, station, forageEntries, existingOutputs);
                        if (added > 0)
                            Plugin.Log?.LogInfo(
                                $"VirtualRecipeLoader: Registered {added} pickable-discovered recipes for {def.stationName} (ZNetScene ready)");
                    }
                }
            }

            RefreshPlayerKnownRecipes();
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
                    physicalStation = sr.physicalStation,
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
            return ToLowerList(def?.cultivatorExclusions);
        }

        private static IReadOnlyList<string> GetForageExclusionsLower(VillagerDef def)
        {
            return ToLowerList(def?.forageExclusions);
        }

        private static IReadOnlyList<string> ToLowerList(List<string> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<string>();
            var lower = new string[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var s = source[i];
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