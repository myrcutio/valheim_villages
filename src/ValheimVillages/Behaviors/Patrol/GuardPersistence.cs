using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Handles saving and loading guard patrol state to/from ZDO.
    /// Persists patrol waypoints, current index, discovery completion, and breach position
    /// so guards don't have to redo circuit discovery after every save/load.
    /// </summary>
    public static class GuardPersistence
    {
        private const string ZdoWaypoints = "vv_guard_waypoints";
        private const string ZdoWpIndex = "vv_guard_wp_index";
        private const string ZdoDiscovery = "vv_guard_discovery";
        private const string ZdoBreach = "vv_guard_breach";
        private const string ZdoHnaRoute = "vv_guard_hna_route";
        private const string ZdoHnaGraph = "vv_hna_graph";

        /// <summary>Save current guard state to ZDO.</summary>
        public static void Save(GuardBehavior guard, ZDO zdo)
        {
            if (guard == null || zdo == null) return;

            var (waypoints, wpIndex, complete, breach, isHna) = guard.GetPersistentState();

            zdo.Set(ZdoDiscovery, complete ? 1 : 0);
            zdo.Set(ZdoWpIndex, wpIndex);
            zdo.Set(ZdoBreach, breach ?? Vector3.zero);
            zdo.Set(ZdoWaypoints, SerializeWaypoints(waypoints));
            zdo.Set(ZdoHnaRoute, isHna ? 1 : 0);
        }

        /// <summary>Save the HNA region graph to the guard's ZDO for cross-player persistence.</summary>
        public static void SaveHnaGraph(ZDO zdo)
        {
            if (zdo == null || !HnaRegionGraph.IsAvailable) return;
            zdo.Set(ZdoHnaGraph, HnaRegionGraph.Serialize());
        }

        /// <summary>
        /// Try to restore the HNA region graph from this guard's ZDO.
        /// Returns true if the graph was successfully restored (now available in memory).
        /// </summary>
        public static bool TryRestoreHnaGraph(ZDO zdo)
        {
            if (zdo == null) return false;
            string data = zdo.GetString(ZdoHnaGraph, "");
            if (string.IsNullOrEmpty(data)) return false;
            return HnaRegionGraph.Restore(data);
        }

        /// <summary>Load guard state from ZDO and restore it.</summary>
        public static void Load(GuardBehavior guard, ZDO zdo)
        {
            if (guard == null || zdo == null) return;

            bool complete = zdo.GetInt(ZdoDiscovery, 0) == 1;
            int wpIndex = zdo.GetInt(ZdoWpIndex, 0);
            var breach = zdo.GetVec3(ZdoBreach, Vector3.zero);
            var waypoints = DeserializeWaypoints(zdo.GetString(ZdoWaypoints, ""));
            bool isHna = zdo.GetInt(ZdoHnaRoute, 0) == 1;

            if (!complete || waypoints == null || waypoints.Count == 0) return;

            guard.RestoreState(waypoints, wpIndex, breach != Vector3.zero ? breach : (Vector3?)null, isHna);
        }

        private static string SerializeWaypoints(List<VillagerWaypoint> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0) return "";

            var parts = new string[waypoints.Count];
            for (int i = 0; i < waypoints.Count; i++)
            {
                var wp = waypoints[i];
                var p = wp.Position;
                var sid = string.IsNullOrEmpty(wp.StrategyId) ? PathingStrategyRegistry.DefaultId : wp.StrategyId;
                int active = wp.Active ? 1 : 0;
                parts[i] = string.Format(CultureInfo.InvariantCulture,
                    "{0:F1},{1:F1},{2:F1},{3},{4}", p.x, p.y, p.z, sid, active);
            }
            return string.Join("|", parts);
        }

        private static List<VillagerWaypoint> DeserializeWaypoints(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;

            var result = new List<VillagerWaypoint>();
            foreach (var part in data.Split('|'))
            {
                var f = part.Split(',');
                if (f.Length >= 3 &&
                    float.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    var strategyId = (f.Length >= 4 && !string.IsNullOrEmpty(f[3])) ? f[3] : PathingStrategyRegistry.DefaultId;
                    bool active = f.Length < 5 || f[4] != "0";
                    var wp = new VillagerWaypoint(new Vector3(x, y, z), strategyId) { Active = active };
                    result.Add(wp);
                }
            }
            return result.Count > 0 ? result : null;
        }
    }
}
