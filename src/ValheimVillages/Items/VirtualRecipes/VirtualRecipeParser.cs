using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Items.VirtualRecipes
{
    /// <summary>
    /// Station template management for virtual recipes.
    /// Creates and caches dummy CraftingStation GameObjects used to associate
    /// virtual recipes with villager stations.
    /// </summary>
    internal static class VirtualRecipeParser
    {
        private static readonly Dictionary<string, CraftingStation> _stationTemplates = new();

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

        internal static CraftingStation GetStationTemplate(string stationName)
        {
            return _stationTemplates.TryGetValue(stationName, out var s) ? s : null;
        }
    }
}
