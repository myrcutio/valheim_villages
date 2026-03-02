using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    /// List panel showing the village map visualization in the Debug tab.
    /// Discovered via NPC tag "listpanel:villagemap".
    /// Shows for any villager with the patrol behavior.
    /// </summary>
    [RegisterListPanel("villagemap", "debug")]
    public class VillageMapPanel : IListPanelUI
    {
        public string Tag => "villagemap";
        public string ParentTab => "debug";

        private Texture2D m_cachedMapTexture;
        private int m_mapWaypointHash;

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>();

            if (villager.villagerInstance)
            {
                items.Add(new TabListItemUI { TabName = "Village Map" });
            }
            return items;
        }

        public TabDetailDataUI GetDetail(int index, VillagerBehaviorBridge villager)
        {
            var patrol = villager.AI?.GetBehavior<PerimeterPatrolBehavior>();
            if (patrol == null) return null;

            var waypoints = patrol.PatrolWaypoints;
            int count = waypoints?.Count ?? 0;

            int hash = ComputeWaypointHash(waypoints);
            if (m_cachedMapTexture == null || hash != m_mapWaypointHash)
            {
                m_mapWaypointHash = hash;
                m_cachedMapTexture = PatrolMapRenderer.Render(
                    waypoints, patrol.BedPosition, villager.transform.position);
            }

            int activeCount = patrol.ActiveWaypointCount;
            int inactiveCount = count - activeCount;

            string source = patrol.IsHnaRoute ? "HNA boundary" : "Discovery";
            string desc = patrol.IsDiscoveryComplete
                ? $"{activeCount} active waypoints | {source}"
                : $"{activeCount} active waypoints | Mapping...";
            if (inactiveCount > 0)
                desc += $"\n{inactiveCount} inactive (pruned)";
            desc += $"\nBed: ({patrol.BedPosition.x:F0}, {patrol.BedPosition.z:F0})";

            return new TabDetailDataUI
            {
                Title = "Village Map",
                Description = desc,
                MapTexture = m_cachedMapTexture,
                ActionText = "Remap",
                OnAction = () =>
                {
                    patrol.ResetDiscovery();
                    ClearMapCache();
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.TopLeft,
                        "Villager will re-map the village");
                    InventoryGui.instance?.Hide();
                }
            };
        }

        public void ClearMapCache()
        {
            m_cachedMapTexture = null;
            m_mapWaypointHash = 0;
        }

        private static int ComputeWaypointHash(
            IReadOnlyList<VillagerWaypoint> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0) return 0;
            int hash = waypoints.Count;
            for (int i = 0; i < waypoints.Count; i++)
            {
                var p = waypoints[i].Position;
                hash = hash * 31 + p.x.GetHashCode();
                hash = hash * 31 + p.z.GetHashCode();
                hash = hash * 31 + (waypoints[i].Active ? 1 : 0);
            }
            return hash;
        }
    }
}
