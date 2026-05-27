using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Helper for rendering per-task maps in the Tasks tab. Composes the patrol
    ///     map renderer with HNA region cells, optional ground-truth path, and
    ///     task-specific pins (e.g. work-order chest locations).
    /// </summary>
    public static class VillageMapPanel
    {
        private static List<Vector3> s_groundTruthPath;
        private static bool s_groundTruthLoaded;

        /// <summary>
        ///     Render a per-task map for a villager, with optional extra pins for task-relevant locations.
        ///     Returns null if no useful map can be drawn.
        /// </summary>
        public static Texture2D RenderForTask(
            VillagerBehaviorBridge villager,
            IReadOnlyList<(Vector3 position, Color color)> extraPins)
        {
            if (villager == null) return null;

            List<Vector3> floodFillCells = null;
            foreach (var graph in RegionGraph.GetAll())
            {
                var centers = graph.GetAllRegionCenters();
                if (centers.Count > 0)
                {
                    if (floodFillCells == null) floodFillCells = new List<Vector3>();
                    floodFillCells.AddRange(centers);
                }
            }

            var groundTruth = LoadGroundTruth();
            var bedPosition = villager.AI?.Villager?.BedPosition ?? Vector3.zero;
            var villagerPosition = villager.transform != null ? villager.transform.position : (Vector3?)null;
            var patrol = villager.AI?.GetBehavior<PerimeterPatrolBehavior>();
            var waypoints = patrol?.PatrolWaypoints;

            return PatrolMapRenderer.Render(
                waypoints,
                bedPosition,
                villagerPosition,
                floodFillCells,
                groundTruth,
                extraPins: extraPins);
        }

        public static List<Vector3> LoadGroundTruth()
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
    }
}
