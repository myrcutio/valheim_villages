using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Attaches a virtual CraftingStation to an NPC GameObject so that
    /// Valheim's crafting UI can be opened with the NPC as the "station."
    /// 
    /// NPC types with recipes (Farmer, TavernKeeper) get specific station
    /// names that match their virtual recipes. All other NPC types get a
    /// generic station used purely for the tab UI (Info/Debug tabs).
    /// </summary>
    public class VillagerStation : MonoBehaviour
    {
        /// <summary>
        /// Maps NPC types that have virtual recipes to their station names.
        /// </summary>
        public static readonly Dictionary<NpcType, string> CraftingStationNames = new()
        {
            { NpcType.Farmer, "$vv_farmer" },
            { NpcType.TavernKeeper, "$vv_tavernkeeper" }
        };

        /// <summary>
        /// Item prefab names used for the station icon in the crafting UI
        /// (e.g. Farmer shows cultivator, Guard shows shield). Null = use game default.
        /// </summary>
        private static readonly Dictionary<NpcType, string> StationIconItems = new()
        {
            { NpcType.Farmer, "Cultivator" },
            { NpcType.Miner, "Pickaxe" },
            { NpcType.Blacksmith, "Hammer" },
            { NpcType.Carpenter, "Hammer" },
            { NpcType.Scout, "Feathers" },
            { NpcType.Trader, "Coins" },
            { NpcType.Guard, "ShieldWood" },
            { NpcType.Mountaineer, "Wishbone" },
            { NpcType.Shipwright, "Hammer" },
            { NpcType.TavernKeeper, "Tankard" }
        };

        /// <summary>Generic station name for NPC types without recipes.</summary>
        public const string GenericStationName = "$vv_villager";

        private CraftingStation m_station;

        /// <summary>The attached CraftingStation component.</summary>
        public CraftingStation Station => m_station;

        /// <summary>
        /// Whether this NPC type has crafting recipes (not just the UI shell).
        /// </summary>
        public static bool HasCraftingRecipes(NpcType type)
        {
            return CraftingStationNames.ContainsKey(type);
        }

        /// <summary>
        /// Returns the station name for the given NPC type.
        /// Crafting types get their specific name; others get the generic name.
        /// </summary>
        public static string GetStationName(NpcType type)
        {
            return CraftingStationNames.TryGetValue(type, out var name)
                ? name
                : GenericStationName;
        }

        /// <summary>
        /// Returns true if the given station name is a virtual villager station.
        /// </summary>
        public static bool IsVirtualStation(string stationName)
        {
            return stationName != null && stationName.StartsWith("$vv_");
        }

        /// <summary>
        /// All NPC types now support a virtual station (for the UI panel).
        /// </summary>
        public static bool HasVirtualStation(NpcType? type)
        {
            return type.HasValue;
        }

        /// <summary>
        /// Initialize the virtual station for the given NPC type.
        /// Adds a CraftingStation component and configures it for UI-only use.
        /// </summary>
        public void Initialize(NpcType npcType)
        {
            var stationName = GetStationName(npcType);

            m_station = gameObject.AddComponent<CraftingStation>();
            m_station.m_name = stationName;
            m_station.m_icon = GetStationIconFor(npcType);
            m_station.m_discoverRange = 0f;
            m_station.m_rangeBuild = 0f;
            m_station.m_craftRequireRoof = false;
            m_station.m_craftRequireFire = false;
            m_station.m_showBasicRecipies = false;
            m_station.m_useDistance = 10f;
            m_station.m_useAnimation = 0;
            m_station.m_areaMarker = null;
            m_station.m_inUseObject = null;
            m_station.m_haveFireObject = null;
            m_station.m_craftItemEffects = new EffectList();
            m_station.m_craftItemDoneEffects = new EffectList();
            m_station.m_repairItemDoneEffects = new EffectList();

            Plugin.Log?.LogInfo(
                $"VillagerStation: Initialized '{stationName}' for {npcType}");
        }

        /// <summary>
        /// Returns the sprite to use as the station icon in the UI, or null for game default.
        /// </summary>
        private static Sprite GetStationIconFor(NpcType npcType)
        {
            if (!StationIconItems.TryGetValue(npcType, out var itemName) || string.IsNullOrEmpty(itemName))
                return null;
            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            if (prefab == null) return null;
            var drop = prefab.GetComponent<ItemDrop>();
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return (icons != null && icons.Length > 0) ? icons[0] : null;
        }
    }
}
