using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.NPCs;
using ValheimVillages.NPCs.AI;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    /// List panel showing the village map visualization in the Debug tab.
    /// Extracted from DebugTab.Guard's map command and detail rendering.
    /// Discovered via NPC tag "listpanel:villagemap".
    /// </summary>
    public class VillageMapPanel : IListPanel
    {
        public string Tag => "villagemap";
        public string ParentTab => "debug";

        private Texture2D m_cachedMapTexture;
        private int m_mapWaypointHash;

        public List<TabListItem> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItem>();
            if (villager.NpcType != NpcType.Guard) return items;

            var guard = villager.AI?.GuardBehavior;
            if (guard == null) return items;

            items.Add(new TabListItem { Name = "Village Map" });
            return items;
        }

        public TabDetailData GetDetail(int index, VillagerBehaviorBridge villager)
        {
            var guard = villager.AI?.GuardBehavior;
            if (guard == null) return null;

            var waypoints = guard.PatrolWaypoints;
            int count = waypoints?.Count ?? 0;

            int hash = ComputeWaypointHash(waypoints);
            if (m_cachedMapTexture == null || hash != m_mapWaypointHash)
            {
                m_mapWaypointHash = hash;
                m_cachedMapTexture = GuardPatrolMapRenderer.Render(
                    waypoints, guard.BedPosition, villager.transform.position);
            }

            int activeCount = guard.ActiveWaypointCount;
            int inactiveCount = count - activeCount;

            string source = guard.IsHnaRoute ? "HNA boundary" : "Discovery";
            string desc = guard.IsDiscoveryComplete
                ? $"{activeCount} active waypoints | {source}"
                : $"{activeCount} active waypoints | Mapping...";
            if (inactiveCount > 0)
                desc += $"\n{inactiveCount} inactive (pruned)";
            desc += $"\nBed: ({guard.BedPosition.x:F0}, {guard.BedPosition.z:F0})";

            return new TabDetailData
            {
                Title = "Village Map",
                Description = desc,
                MapTexture = m_cachedMapTexture,
                ActionText = "Remap",
                OnAction = () =>
                {
                    guard.ResetDiscovery();
                    ClearMapCache();
                    Player.m_localPlayer?.Message(
                        MessageHud.MessageType.TopLeft,
                        "Guard will re-map the village");
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
