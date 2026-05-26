using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Tabs
{
    /// <summary>
    ///     Tab showing villager information.  Provides favourite places as
    ///     list items (left pane) and place details + "Mark" action (right pane).
    /// </summary>
    [RegisterTab("info", Order = 0)]
    public class InfoTab : IVillagerTabUI
    {
        private List<KnownLocation> m_topLocations = new();
        public string TabName => "Info";

        public void OnSelected(VillagerBehaviorBridge villager)
        {
            RefreshLocations(villager);
        }

        public void OnDeselected()
        {
            m_topLocations.Clear();
        }

        public void OnUpdate(VillagerBehaviorBridge villager)
        {
            RefreshLocations(villager);
        }

        #region Place Data

        private TabDetailDataUI GetPlaceDetail(
            int index, VillagerBehaviorBridge villager)
        {
            var loc = m_topLocations[index];
            var dist = villager != null
                ? Vector3.Distance(
                    villager.transform.position, loc.Position)
                : 0f;

            var shelter = loc.HasShelter ? " (sheltered)" : "";
            var desc = $"{GetLocationDescription(loc)}{shelter}\n" +
                       $"Distance: {dist:F0}m\n" +
                       $"Comfort: {loc.ComfortValue:F0}";

            var captured = loc;
            return new TabDetailDataUI
            {
                Title = GetShortName(loc),
                Description = desc,
                ActionText = "Mark on Map",
                OnAction = () => AddMapPin(captured, villager),
            };
        }

        #endregion

        #region IVillagerTab — List + Detail

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>();
            foreach (var loc in m_topLocations)
                items.Add(new TabListItemUI
                {
                    TabName = $"{GetLocationIcon(loc.Type)} {GetShortName(loc)}",
                    Icon = null, // could map to real sprites later
                });

            // Add ability entries below the places
            AddAbilityItems(items, villager);
            return items;
        }

        public TabDetailDataUI GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            // Place items
            if (index >= 0 && index < m_topLocations.Count)
                return GetPlaceDetail(index, villager);

            // Ability items (after places)
            var abilityIdx = index - m_topLocations.Count;
            return GetAbilityDetail(abilityIdx, villager);
        }

        #endregion

        #region Panel-Driven Ability Data

        private static readonly List<IListPanel> s_panels = new();

        /// <summary>Register an IListPanel for this tab. Called from Plugin startup.</summary>
        public static void RegisterPanel(IListPanel panel)
        {
            if (panel.ParentTab == "info" && !s_panels.Contains(panel))
                s_panels.Add(panel);
        }

        private readonly List<(IListPanel panel, int startIdx, int count)> m_panelRanges = new();

        private void AddAbilityItems(
            List<TabListItemUI> items, VillagerBehaviorBridge villager)
        {
            m_panelRanges.Clear();
            foreach (var panel in s_panels)
                if (panel is IListPanelUI panelUI)
                {
                    var panelItems = panelUI.GetListItems(villager);
                    if (panelItems.Count > 0)
                    {
                        m_panelRanges.Add((panel, items.Count, panelItems.Count));
                        foreach (var p in panelItems) items.Add(p);
                    }
                }
        }

        private TabDetailDataUI GetAbilityDetail(
            int abilityIdx, VillagerBehaviorBridge villager)
        {
            var globalIdx = m_topLocations.Count + abilityIdx;
            foreach (var (panel, startIdx, count) in m_panelRanges)
                if (globalIdx >= startIdx && globalIdx < startIdx + count)
                    return panel is IListPanelUI panelUI ? panelUI.GetDetail(globalIdx - startIdx, villager) : null;
            return null;
        }

        #endregion

        #region Helpers

        private void RefreshLocations(VillagerBehaviorBridge villager)
        {
            m_topLocations.Clear();
            if (villager?.Memory == null) return;
            m_topLocations = villager.Memory.KnownLocations
                .Select(l => new { Loc = l, Score = ScoreLocation(l) })
                .OrderByDescending(x => x.Score)
                .Take(5)
                .Select(x => x.Loc)
                .ToList();
        }

        private static float ScoreLocation(KnownLocation loc)
        {
            var s = loc.Type switch
            {
                LocationType.Bed => 100f, LocationType.Fire => 50f,
                LocationType.Farm => 30f, LocationType.Animals => 30f,
                LocationType.Shelter => 10f,
                _ => 0f,
            };
            if (loc.HasShelter) s += 15f;
            s += loc.ComfortValue * 10f;
            return s;
        }

        private static string GetShortName(KnownLocation loc)
        {
            return loc.Type switch
            {
                LocationType.Bed => "Cozy Bed",
                LocationType.Fire => loc.HasShelter ? "Warm Hearth" : "Campfire",
                LocationType.Table => "Gathering Table",
                LocationType.Shelter => "Dry Spot",
                LocationType.Farm => "Open Fields",
                LocationType.Animals => "Friendly Creatures",
                _ => "Interesting Spot",
            };
        }

        private static string GetLocationIcon(LocationType t)
        {
            return t switch
            {
                LocationType.Bed => "[Bed]",
                LocationType.Fire => "[Fire]",
                LocationType.Table => "[Table]",
                LocationType.Shelter => "[Roof]",
                LocationType.Farm => "[Field]",
                LocationType.Animals => "[Beasts]",
                _ => "[?]",
            };
        }

        private static string GetLocationDescription(KnownLocation loc)
        {
            return loc.Type switch
            {
                LocationType.Bed => "A cozy bed",
                LocationType.Fire =>
                    loc.HasShelter ? "A warm hearth" : "A campfire",
                LocationType.Table => "A gathering table",
                LocationType.Shelter => "A dry spot",
                LocationType.Farm => "Open fields",
                LocationType.Animals => "Friendly creatures",
                _ => "An interesting spot",
            };
        }

        private static void AddMapPin(
            KnownLocation loc, VillagerBehaviorBridge villager)
        {
            var minimap = Minimap.instance;
            if (minimap == null)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.TopLeft, "Map not available");
                return;
            }

            var desc = GetLocationDescription(loc);
            var name = villager?.GetComponent<Humanoid>()?.m_name
                       ?? "Villager";
            try
            {
                var method = typeof(Minimap).GetMethod("AddPin",
                    BindingFlags.Public |
                    BindingFlags.Instance,
                    null,
                    new[]
                    {
                        typeof(Vector3), typeof(Minimap.PinType),
                        typeof(string), typeof(bool), typeof(bool),
                    },
                    null);
                method?.Invoke(minimap, new object[]
                {
                    loc.Position, Minimap.PinType.Icon3,
                    $"{name}: {desc}", true, false,
                });
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.TopLeft, $"Marked: {desc}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Failed to add map pin: {ex.Message}");
            }
        }

        #endregion
    }
}