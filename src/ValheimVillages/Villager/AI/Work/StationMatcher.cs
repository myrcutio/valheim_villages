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
        /// </summary>
        public static Recipe FindRecipe(string itemPrefabName, string stationName)
        {
            if (ObjectDB.instance == null) return null;

            return ObjectDB.instance.m_recipes.FirstOrDefault(r =>
                r.m_item != null &&
                r.m_item.gameObject.name == itemPrefabName &&
                r.m_craftingStation != null &&
                r.m_craftingStation.m_name == stationName &&
                r.m_enabled);
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
    }
}