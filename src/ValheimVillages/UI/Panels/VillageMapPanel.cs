using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     List panel showing the village map visualization in the Debug tab.
    ///     Discovered via NPC tag "listpanel:villagemap".
    ///     Shows for any villager with the patrol behavior.
    /// </summary>
    [RegisterListPanel("villagemap", "debug")]
    public class VillageMapPanel : IListPanelUI
    {
        private static List<Vector3> s_groundTruthPath;
        private static bool s_groundTruthLoaded;

        private Texture2D m_cachedMapTexture;
        private int m_mapWaypointHash;
        public string Tag => "villagemap";
        public string ParentTab => "debug";

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>();

            if (villager.villagerInstance) items.Add(new TabListItemUI { TabName = "Village Map" });
            return items;
        }

        public TabDetailDataUI GetDetail(int index, VillagerBehaviorBridge villager)
        {
            var patrol = villager.AI?.GetBehavior<PerimeterPatrolBehavior>();
            if (patrol == null) return null;

            var waypoints = patrol.PatrolWaypoints;
            var count = waypoints?.Count ?? 0;

            List<Vector3> floodFillCells = null;
            var regionCount = 0;
            foreach (var graph in RegionGraph.GetAll())
            {
                var centers = graph.GetAllRegionCenters();
                if (centers.Count > 0)
                {
                    if (floodFillCells == null) floodFillCells = new List<Vector3>();
                    floodFillCells.AddRange(centers);
                    regionCount += graph.RegionCount;
                }
            }

            var groundTruth = LoadGroundTruth();

            var hash = ComputeWaypointHash(waypoints, floodFillCells?.Count ?? 0, groundTruth?.Count ?? 0);
            if (m_cachedMapTexture == null || hash != m_mapWaypointHash)
            {
                m_mapWaypointHash = hash;
                m_cachedMapTexture = PatrolMapRenderer.Render(
                    waypoints, patrol.BedPosition, villager.transform.position,
                    floodFillCells, groundTruth);
            }

            var activeCount = patrol.ActiveWaypointCount;
            var inactiveCount = count - activeCount;

            var source = patrol.IsHnaRoute ? "HNA boundary" : "Discovery";
            var desc = patrol.IsDiscoveryComplete
                ? $"{activeCount} active waypoints | {source}"
                : $"{activeCount} active waypoints | Mapping...";
            if (inactiveCount > 0)
                desc += $"\n{inactiveCount} inactive (pruned)";
            if (regionCount > 0)
                desc += $"\nHNA cells: {regionCount}";
            if (groundTruth != null)
                desc += $"\nGround truth: {groundTruth.Count} points (magenta)";
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
                },
            };
        }

        public void ClearMapCache()
        {
            m_cachedMapTexture = null;
            m_mapWaypointHash = 0;
        }

        private static List<Vector3> LoadGroundTruth()
        {
            if (s_groundTruthLoaded) return s_groundTruthPath;

            try
            {
                s_groundTruthLoaded = true;

                var scriptDir = Path.Combine(Paths.BepInExRootPath, "scripts");
                if (!Directory.Exists(scriptDir)) return null;

                var jsonPath = Path.Combine(scriptDir, "hna_walkable_path.json");
                if (!File.Exists(jsonPath))
                {
                    Plugin.Log?.LogInfo($"[VillageMap] No ground truth file at {jsonPath}");
                    return null;
                }

                var json = File.ReadAllText(jsonPath);
                s_groundTruthPath = ParsePositionsJson(json);
                Plugin.Log?.LogInfo(
                    $"[VillageMap] Loaded ground truth: {s_groundTruthPath.Count} points from {jsonPath}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[VillageMap] Failed to load ground truth: {ex.Message}");
                s_groundTruthLoaded = true;
            }

            return s_groundTruthPath;
        }

        /// <summary>
        ///     Minimal JSON parser for the positions array in hna_walkable_path.json.
        ///     Expects format: { "positions": [ [x,y,z], [x,y,z], ... ] }
        /// </summary>
        internal static List<Vector3> ParsePositionsJson(string json)
        {
            var result = new List<Vector3>();

            var posIdx = json.IndexOf("\"positions\"");
            if (posIdx < 0) return result;

            var arrStart = json.IndexOf('[', posIdx);
            if (arrStart < 0) return result;

            var depth = 0;
            var tripleStart = -1;
            for (var i = arrStart; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '[')
                {
                    depth++;
                    if (depth == 2) tripleStart = i + 1;
                }
                else if (c == ']')
                {
                    if (depth == 2 && tripleStart >= 0)
                    {
                        var triplet = json.Substring(tripleStart, i - tripleStart);
                        var parts = triplet.Split(',');
                        if (parts.Length >= 3 &&
                            float.TryParse(parts[0].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var x) &&
                            float.TryParse(parts[1].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var y) &&
                            float.TryParse(parts[2].Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var z))
                            result.Add(new Vector3(x, y, z));
                    }

                    depth--;
                    if (depth <= 0) break;
                }
            }

            return result;
        }

        private static int ComputeWaypointHash(
            IReadOnlyList<VillagerWaypoint> waypoints, int floodFillCount, int groundTruthCount)
        {
            var hash = floodFillCount * 7919 + groundTruthCount * 6271;
            if (waypoints == null || waypoints.Count == 0) return hash;
            hash += waypoints.Count;
            for (var i = 0; i < waypoints.Count; i++)
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