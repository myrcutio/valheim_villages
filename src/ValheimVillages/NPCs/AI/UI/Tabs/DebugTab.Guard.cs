using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.NPCs.AI.Guards;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Guard-specific debug commands: village map visualization and remap.
    /// </summary>
    public partial class DebugTab
    {
        private Texture2D m_cachedMapTexture;
        private int m_mapWaypointHash;

        private void ClearMapCache()
        {
            m_cachedMapTexture = null;
            m_mapWaypointHash = 0;
        }

        private void AddGuardCommands(VillagerBehaviorBridge villager)
        {
            var guard = villager.AI?.GuardBehavior;
            if (guard == null) return;

            m_commands.Add(new DebugCommand
            {
                Name = "Village Map",
                IsMapCommand = true,
                Guard = guard,
                GuardPosition = villager.transform.position,
            });
        }

        private TabDetailData GetMapDetail(DebugCommand cmd)
        {
            var guard = cmd.Guard;
            var waypoints = guard?.PatrolWaypoints;
            int count = waypoints?.Count ?? 0;

            int hash = ComputeWaypointHash(waypoints);
            if (m_cachedMapTexture == null || hash != m_mapWaypointHash)
            {
                m_mapWaypointHash = hash;
                m_cachedMapTexture = GuardPatrolMapRenderer.Render(
                    waypoints, guard.BedPosition, cmd.GuardPosition);
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
                    Msg("Guard will re-map the village");
                    InventoryGui.instance?.Hide();
                }
            };
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
