using System.Collections.Generic;
using System.Linq;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Maps NPC types to crafting station names and provides recipe lookup.
    /// </summary>
    public static class StationMatcher
    {
        private static readonly Dictionary<NpcType, string[]> NpcStationMap = new()
        {
            { NpcType.Blacksmith, new[] { "$piece_forge" } },
            { NpcType.Carpenter,  new[] { "$piece_workbench" } },
            { NpcType.Farmer,     new[] { "$piece_workbench", "$piece_cauldron", "$vv_farmer" } },
            { NpcType.TavernKeeper, new[] { "$vv_tavernkeeper" } }
        };

        /// <summary>
        /// Returns true if this NPC type can execute work orders.
        /// </summary>
        public static bool IsWorkerType(NpcType? type)
        {
            return type.HasValue && NpcStationMap.ContainsKey(type.Value);
        }

        /// <summary>
        /// Returns the station name strings this NPC type can work at.
        /// </summary>
        public static string[] GetStationNames(NpcType type)
        {
            return NpcStationMap.TryGetValue(type, out var names) ? names : System.Array.Empty<string>();
        }

        /// <summary>
        /// Checks if an NPC type can work at the given station (by station m_name).
        /// </summary>
        public static bool CanWorkStation(NpcType type, string stationName)
        {
            return NpcStationMap.TryGetValue(type, out var names) &&
                   names.Contains(stationName);
        }

        /// <summary>
        /// Finds a recipe in ObjectDB that produces the given item at the given station.
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
        /// Finds a recipe by item prefab name, checking all stations the NPC can use.
        /// </summary>
        public static Recipe FindRecipeForNpc(string itemPrefabName, NpcType type)
        {
            foreach (var station in GetStationNames(type))
            {
                var recipe = FindRecipe(itemPrefabName, station);
                if (recipe != null) return recipe;
            }
            return null;
        }
    }
}
