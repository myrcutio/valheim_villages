using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Abilities;
using ValheimVillages.NPCs;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Tab showing villager information.  Provides favourite places as
    /// list items (left pane) and place details + "Mark" action (right pane).
    /// </summary>
    public class InfoTab : IVillagerTab
    {
        public string Name => "Info";

        private List<KnownLocation> m_topLocations = new();

        public void OnSelected(VillagerBehaviorBridge villager) =>
            RefreshLocations(villager);

        public void OnDeselected() => m_topLocations.Clear();

        public void OnUpdate(VillagerBehaviorBridge villager) =>
            RefreshLocations(villager);

        #region IVillagerTab — List + Detail

        public List<TabListItem> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItem>();
            foreach (var loc in m_topLocations)
            {
                items.Add(new TabListItem
                {
                    Name = $"{GetLocationIcon(loc.Type)} {GetShortName(loc)}",
                    Icon = null // could map to real sprites later
                });
            }

            // Add ability entries below the places
            AddAbilityItems(items, villager);
            return items;
        }

        public TabDetailData GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            // Place items
            if (index >= 0 && index < m_topLocations.Count)
                return GetPlaceDetail(index, villager);

            // Ability items (after places)
            int abilityIdx = index - m_topLocations.Count;
            return GetAbilityDetail(abilityIdx, villager);
        }

        #endregion

        #region Place Data

        private TabDetailData GetPlaceDetail(
            int index, VillagerBehaviorBridge villager)
        {
            var loc = m_topLocations[index];
            float dist = villager != null
                ? Vector3.Distance(
                    villager.transform.position, loc.Position)
                : 0f;

            string shelter = loc.HasShelter ? " (sheltered)" : "";
            string desc = $"{GetLocationDescription(loc)}{shelter}\n" +
                $"Distance: {dist:F0}m\n" +
                $"Comfort: {loc.ComfortValue:F0}";

            var captured = loc;
            return new TabDetailData
            {
                Title = GetShortName(loc),
                Description = desc,
                ActionText = "Mark on Map",
                OnAction = () => AddMapPin(captured, villager)
            };
        }

        #endregion

        #region Ability Data

        private void AddAbilityItems(
            List<TabListItem> items, VillagerBehaviorBridge villager)
        {
            if (villager.NpcType == NpcType.Guard &&
                villager.AI?.GuardBehavior != null)
            {
                items.Add(new TabListItem { Name = "[Guard] Patrol Status" });
            }
            else if (villager.NpcType == NpcType.Mountaineer)
            {
                items.Add(new TabListItem { Name = "[Technique] Mountain Stride" });
            }
        }

        private TabDetailData GetAbilityDetail(
            int abilityIdx, VillagerBehaviorBridge villager)
        {
            if (abilityIdx == 0 &&
                villager.NpcType == NpcType.Guard)
                return GetGuardDetail(villager);

            if (abilityIdx == 0 &&
                villager.NpcType == NpcType.Mountaineer)
                return GetMountaineerDetail(villager);

            return null;
        }

        private TabDetailData GetGuardDetail(VillagerBehaviorBridge villager)
        {
            var guard = villager.AI?.GuardBehavior;
            if (guard == null) return null;

            if (!guard.IsDiscoveryComplete)
            {
                return new TabDetailData
                {
                    Title = "Guard Duty",
                    Description = "Mapping the village perimeter...",
                };
            }

            if (guard.IsAlarmed)
            {
                return new TabDetailData
                {
                    Title = "Guard Duty — BREACH",
                    Description = "A breach has been detected!\n" +
                        "Repair the wall gap to resume patrol.",
                    ActionText = "Show Breach",
                    OnAction = () =>
                    {
                        guard.WalkToBreach();
                        Player.m_localPlayer?.Message(
                            MessageHud.MessageType.TopLeft,
                            "The guard will walk to the breach.");
                    }
                };
            }

            return new TabDetailData
            {
                Title = "Guard Duty",
                Description = $"Patrolling ({guard.WaypointCount} waypoints).\n" +
                    "No breaches detected."
            };
        }

        private TabDetailData GetMountaineerDetail(
            VillagerBehaviorBridge villager)
        {
            bool learned = VillagerAbilityManager.HasLearnedMountainStride();

            if (!learned)
            {
                return new TabDetailData
                {
                    Title = "Mountain Stride",
                    Description = "The Mountaineer can teach you to " +
                        "traverse steep terrain without sliding.",
                    ActionText = "Learn",
                    OnAction = () =>
                        VillagerAbilityManager.LearnMountainStride()
                };
            }

            bool active = VillagerAbilityManager.IsActive();
            float cd = VillagerAbilityManager.GetCooldownRemaining();
            string status = active
                ? "Active — you won't slide."
                : cd > 0f
                    ? $"Ready in {Mathf.CeilToInt(cd / 60f)}m. Press R."
                    : "Press R to activate (5 min).";

            return new TabDetailData
            {
                Title = "Mountain Stride",
                Description = status
            };
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
            float s = loc.Type switch
            {
                LocationType.Bed => 100f, LocationType.Fire => 50f,
                LocationType.Chair => 40f, LocationType.Table => 35f,
                LocationType.Farm => 30f, LocationType.Animals => 30f,
                LocationType.Shelter => 10f, LocationType.Patrol => 5f,
                _ => 0f
            };
            if (loc.HasShelter) s += 15f;
            s += loc.ComfortValue * 10f;
            return s;
        }

        private static string GetShortName(KnownLocation loc) => loc.Type switch
        {
            LocationType.Bed => "Cozy Bed",
            LocationType.Fire => loc.HasShelter ? "Warm Hearth" : "Campfire",
            LocationType.Chair => "Comfortable Seat",
            LocationType.Table => "Gathering Table",
            LocationType.Shelter => "Dry Spot",
            LocationType.Farm => "Open Fields",
            LocationType.Animals => "Friendly Creatures",
            LocationType.Patrol => "Scenic Path",
            _ => "Interesting Spot"
        };

        private static string GetLocationIcon(LocationType t) => t switch
        {
            LocationType.Bed => "[Bed]",
            LocationType.Fire => "[Fire]",
            LocationType.Chair => "[Seat]",
            LocationType.Table => "[Table]",
            LocationType.Shelter => "[Roof]",
            LocationType.Farm => "[Field]",
            LocationType.Animals => "[Beasts]",
            LocationType.Patrol => "[Path]",
            _ => "[?]"
        };

        private static string GetLocationDescription(KnownLocation loc) =>
            loc.Type switch
            {
                LocationType.Bed => "A cozy bed",
                LocationType.Fire =>
                    loc.HasShelter ? "A warm hearth" : "A campfire",
                LocationType.Chair => "A comfortable seat",
                LocationType.Table => "A gathering table",
                LocationType.Shelter => "A dry spot",
                LocationType.Farm => "Open fields",
                LocationType.Animals => "Friendly creatures",
                LocationType.Patrol => "A scenic path",
                _ => "An interesting spot"
            };

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

            string desc = GetLocationDescription(loc);
            string name = villager?.GetComponent<Humanoid>()?.m_name
                ?? "Villager";
            try
            {
                var method = typeof(Minimap).GetMethod("AddPin",
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(Vector3), typeof(Minimap.PinType),
                        typeof(string), typeof(bool), typeof(bool) },
                    null);
                method?.Invoke(minimap, new object[]
                {
                    loc.Position, Minimap.PinType.Icon3,
                    $"{name}: {desc}", true, false
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
