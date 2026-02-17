using System.Collections.Generic;
using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    /// List panel showing guard patrol status in the Info tab.
    /// Extracted from InfoTab's guard-specific ability section.
    /// Discovered via NPC tag "listpanel:guardstatus".
    /// </summary>
    [RegisterListPanel("guardstatus", "info")]
    public class GuardStatusPanel : IListPanel
    {
        public string Tag => "guardstatus";
        public string ParentTab => "info";

        public List<TabListItem> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItem>();
            if (villager.NpcType != NpcType.Guard) return items;

            var guard = villager.AI?.GuardBehavior;
            if (guard == null) return items;

            items.Add(new TabListItem { Name = "[Guard] Patrol Status" });
            return items;
        }

        public TabDetailData GetDetail(int index, VillagerBehaviorBridge villager)
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
    }
}
