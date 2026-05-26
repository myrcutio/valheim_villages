using System.Collections.Generic;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     List panel showing patrol status in the Info tab.
    ///     Discovered via NPC tag "listpanel:patrolstatus".
    ///     Shows for any villager with the patrol behavior.
    /// </summary>
    [RegisterListPanel("patrolstatus", "info")]
    public class PatrolStatusPanel : IListPanel
    {
        public string Tag => "patrolstatus";
        public string ParentTab => "info";

        public List<TabListItem> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItem>();
            var patrol = villager.AI?.GetBehavior<PerimeterPatrolBehavior>();
            if (patrol == null) return items;

            items.Add(new TabListItem { TabName = "Patrol Status" });
            return items;
        }

        public TabDetailData GetDetail(int index, VillagerBehaviorBridge villager)
        {
            var patrol = villager.AI?.GetBehavior<PerimeterPatrolBehavior>();
            if (patrol == null) return null;

            if (!patrol.IsDiscoveryComplete)
                return new TabDetailData
                {
                    Title = "Patrol Duty",
                    Description = "Mapping the village perimeter...",
                };

            return new TabDetailData
            {
                Title = "Patrol Duty",
                Description = $"Patrolling ({patrol.WaypointCount} waypoints).",
            };
        }
    }
}