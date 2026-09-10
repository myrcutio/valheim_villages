using System;
using System.Linq;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Reads workStations from villager JSON definitions to determine
    ///     which crafting stations each villager type can work at.
    /// </summary>
    public static class StationMatcher
    {
        /// <summary>
        ///     Returns the station name strings this villager type can work at.
        /// </summary>
        public static string[] GetStationNames(string villagerType)
        {
            var def = VillagerRegistry.Get(villagerType);
            return def?.workStations != null && def.workStations.Count > 0
                ? def.workStations.ToArray()
                : Array.Empty<string>();
        }

        /// <summary>
        ///     Checks if a villager type can work at the given station (by station m_name).
        /// </summary>
        public static bool CanWorkStation(string villagerType, string stationName)
        {
            var def = VillagerRegistry.Get(villagerType);
            return def?.workStations != null && def.workStations.Contains(stationName);
        }

        /// <summary>
        ///     Finds a recipe in ObjectDB that produces the given item at the given station.
        ///
        ///     <para>A recipe with NO crafting station (hand-craftable — Torch, for example) is
        ///     treated as satisfiable at any station the villager works at. Requiring
        ///     <c>m_craftingStation != null</c> made "needs no station" indistinguishable from
        ///     "no match", so a carpenter standing at a workbench was told
        ///     <c>No recipe for 'Torch'</c> even though the recipe existed and needed nothing
        ///     the villager lacked.</para>
        /// </summary>
        public static Recipe FindRecipe(string itemPrefabName, string stationName)
        {
            if (ObjectDB.instance == null) return null;

            return ObjectDB.instance.m_recipes.FirstOrDefault(r =>
                r.m_item != null &&
                r.m_item.gameObject.name == itemPrefabName &&
                r.m_enabled &&
                (r.m_craftingStation == null ||
                 r.m_craftingStation.m_name == stationName));
        }

        /// <summary>
        ///     Finds a recipe by item prefab name, checking all stations the villager type can use.
        /// </summary>
        public static Recipe FindRecipeForNpc(string itemPrefabName, string villagerType)
        {
            foreach (var station in GetStationNames(villagerType))
            {
                var recipe = FindRecipe(itemPrefabName, station);
                if (recipe != null) return recipe;
            }

            return null;
        }

        /// <summary>
        ///     Resolve the recipe behind a work order from the order's own (item, station) pair.
        ///
        ///     <para>Physical stations ($piece_forge, ...) name a real ObjectDB CraftingStation and
        ///     match directly. Virtual villager stations ($vv_blacksmith, ...) do not exist in
        ///     ObjectDB at all, so they resolve through the villager type that owns the station —
        ///     the same route <c>work_order_scan</c> takes. Callers that only hold a work-order
        ///     token (which carries wo_item + wo_station and nothing else) need this; callers that
        ///     already know the villager should use <see cref="FindRecipeForNpc" />.</para>
        /// </summary>
        public static Recipe FindRecipeForOrder(string itemPrefabName, string stationName)
        {
            if (string.IsNullOrEmpty(stationName)) return null;

            var direct = FindRecipe(itemPrefabName, stationName);
            if (direct != null) return direct;

            var villagerType = VillagerTypeForStation(stationName);
            return villagerType != null
                ? FindRecipeForNpc(itemPrefabName, villagerType)
                : null;
        }

        /// <summary>The villager type whose virtual station carries this name, or null.</summary>
        public static string VillagerTypeForStation(string stationName)
        {
            foreach (var kv in VillagerRegistry.Definitions)
                if (kv.Value?.stationName == stationName)
                    return kv.Value.type;
            return null;
        }
    }
}