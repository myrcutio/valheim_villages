using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Handles saving and loading patrol state to/from ZDO.
    ///     Persists patrol waypoints, current index, and discovery completion
    ///     so patrollers don't have to redo discovery after every save/load.
    /// </summary>
    public static class PatrolPersistence
    {
        private const string ZdoWaypoints = "vv_guard_waypoints";
        private const string ZdoWpIndex = "vv_guard_wp_index";
        private const string ZdoDiscovery = "vv_guard_discovery";
        private const string ZdoHnaRoute = "vv_guard_hna_route";
        private const string ZdoHnaGraph = "vv_hna_graph";

        /// <summary>Save current patrol state to ZDO.</summary>
        public static void Save(PatrolStateMachine patrol, ZDO zdo)
        {
            if (patrol == null || zdo == null) return;

            var (waypoints, wpIndex, complete, isHna) = patrol.GetPersistentState();

            zdo.Set(ZdoDiscovery, complete ? 1 : 0);
            zdo.Set(ZdoWpIndex, wpIndex);
            zdo.Set(ZdoWaypoints, SerializeWaypoints(waypoints));
            zdo.Set(ZdoHnaRoute, isHna ? 1 : 0);
        }

        /// <summary>Save the HNA region graph for a specific village to ZDO.</summary>
        public static void SaveHnaGraph(ZDO zdo, string villageKey)
        {
            if (zdo == null) return;
            var graph = RegionGraph.Get(villageKey);
            if (graph == null || !graph.IsAvailable) return;
            zdo.Set(ZdoHnaGraph, graph.Serialize());
        }

        /// <summary>
        ///     Try to restore the HNA region graph from ZDO into the registry
        ///     under the given village key. If the persisted payload is a legacy
        ///     non-v2 format (or otherwise unparseable), the ZDO entry is wiped
        ///     so the next access triggers a fresh hna_partition build.
        /// </summary>
        public static bool TryRestoreHnaGraph(ZDO zdo, string villageKey)
        {
            if (zdo == null) return false;
            var data = zdo.GetString(ZdoHnaGraph);
            if (string.IsNullOrEmpty(data)) return false;
            var graph = RegionGraph.GetOrCreate(villageKey);
            if (graph.Restore(data)) return true;

            // Restore failed: data is present but the parser rejected it
            // (most often a legacy v1 grid payload). Wipe so the patrol
            // behavior falls through to its existing rebuild path.
            Plugin.Log?.LogInfo(
                "[Region] Wiping legacy/unparseable HNA graph from ZDO " +
                $"(key={villageKey}, bytes={data.Length}); will rebuild on next request");
            zdo.Set(ZdoHnaGraph, "");
            return false;
        }

        /// <summary>Load patrol state from ZDO and restore it.</summary>
        public static void Load(PatrolStateMachine patrol, ZDO zdo)
        {
            if (patrol == null || zdo == null) return;

            var complete = zdo.GetInt(ZdoDiscovery) == 1;
            var wpIndex = zdo.GetInt(ZdoWpIndex);
            var waypoints = DeserializeWaypoints(zdo.GetString(ZdoWaypoints));
            var isHna = zdo.GetInt(ZdoHnaRoute) == 1;

            if (!complete || waypoints == null || waypoints.Count == 0) return;

            patrol.RestoreState(waypoints, wpIndex, isHna);
        }

        private static string SerializeWaypoints(List<VillagerWaypoint> waypoints)
        {
            if (waypoints == null || waypoints.Count == 0) return "";

            var parts = new string[waypoints.Count];
            for (var i = 0; i < waypoints.Count; i++)
            {
                var wp = waypoints[i];
                var p = wp.Position;
                var sid = string.IsNullOrEmpty(wp.StrategyId) ? VillagerWaypoint.DefaultStrategyId : wp.StrategyId;
                var active = wp.Active ? 1 : 0;
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
                    var strategyId = f.Length >= 4 && !string.IsNullOrEmpty(f[3])
                        ? f[3]
                        : VillagerWaypoint.DefaultStrategyId;
                    var active = f.Length < 5 || f[4] != "0";
                    var wp = new VillagerWaypoint(new Vector3(x, y, z), strategyId) { Active = active };
                    result.Add(wp);
                }
            }

            return result.Count > 0 ? result : null;
        }
    }
}