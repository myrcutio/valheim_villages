using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Tags;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.Station
{
    /// <summary>
    /// Attaches a virtual CraftingStation to an NPC GameObject so that
    /// Valheim's crafting UI can be opened with the NPC as the "station."
    /// 
    /// NPC types with a "tab:workorder" tag get specific station names
    /// that match their virtual recipes. All other NPC types get a
    /// generic station used purely for the tab UI (Info/Debug tabs).
    /// </summary>
    public class VillagerStation : MonoBehaviour
    {
        /// <summary>
        /// Returns the virtual station name for this NPC type from the registry,
        /// or null if it has no virtual station.
        /// </summary>
        private static string GetVirtualStationName(string villagerType)
        {
            var def = VillagerRegistry.Get(villagerType);
            return !string.IsNullOrEmpty(def?.stationName) ? def.stationName : null;
        }

        /// <summary>Generic station name for NPC types without recipes.</summary>
        public const string GenericStationName = "$vv_villager";

        private CraftingStation m_station;

        /// <summary>The attached CraftingStation component.</summary>
        public CraftingStation Station => m_station;

        /// <summary>
        /// Whether this NPC type should show the work orders tab (Craft/Upgrade).
        /// Driven by the "tab:workorder" tag in the villager's JSON definition.
        /// </summary>
        public static bool HasCraftingRecipes(string villagerType)
        {
            var def = VillagerRegistry.Get(villagerType);
            return def?.tags != null && TagParser.HasTag(def.tags, "tab", "workorder");
        }

        /// <summary>
        /// Returns the station name for the given NPC type.
        /// Types with a virtual station get their specific name; others get the generic name.
        /// </summary>
        public static string GetStationName(string villagerType)
        {
            return GetVirtualStationName(villagerType) ?? GenericStationName;
        }

        /// <summary>
        /// Returns true if the given station name is a virtual villager station.
        /// </summary>
        public static bool IsVirtualStation(string stationName)
        {
            return stationName != null && stationName.StartsWith("$vv_");
        }

        /// <summary>
        /// Initialize the virtual station for the given NPC type.
        /// Adds a CraftingStation component and configures it for UI-only use.
        /// </summary>
        public void Initialize(string villagerType)
        {
            var stationName = GetStationName(villagerType);

            m_station = gameObject.AddComponent<CraftingStation>();
            m_station.m_name = stationName;
            m_station.m_icon = GetStationIconFor(villagerType);
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
                $"VillagerStation: Initialized '{stationName}' for {villagerType}");
        }

        /// <summary>
        /// Returns the sprite to use as the station icon in the UI, or null for game default.
        /// Uses the stationIcon field from the villager's JSON definition.
        /// </summary>
        private static Sprite GetStationIconFor(string villagerType)
        {
            var def = VillagerRegistry.Get(villagerType);
            var itemName = def?.stationIcon;
            if (string.IsNullOrEmpty(itemName))
                return null;
            var prefab = ObjectDB.instance?.GetItemPrefab(itemName);
            if (prefab == null) return null;
            var drop = prefab.GetComponent<ItemDrop>();
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return (icons != null && icons.Length > 0) ? icons[0] : null;
        }
    }
}
