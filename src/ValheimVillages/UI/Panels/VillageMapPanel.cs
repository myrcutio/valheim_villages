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
            IReadOnlyList<(Vector3 position, Color color)> pins)
        {
            if (villager == null) return null;
            return PatrolMapRenderer.RenderMinimal(GetPerimeter(villager), pins);
        }

        /// <summary>
        ///     The village outline for the map. Prefers the villager's patrol
        ///     route (already a perimeter loop); otherwise outlines the walkable
        ///     region cells with a convex hull so non-patrollers still get a shape.
        /// </summary>
        private static List<Vector3> GetPerimeter(VillagerBehaviorBridge villager)
        {
            var patrol = villager.AI?.GetBehavior<PerimeterPatrolBehavior>();
            var waypoints = patrol?.PatrolWaypoints;
            if (waypoints != null)
            {
                var active = new List<Vector3>();
                foreach (var w in waypoints)
                    if (w.Active)
                        active.Add(w.Position);
                if (active.Count >= 3) return active;
            }

            var cells = new List<Vector3>();
            foreach (var graph in RegionGraph.GetAll())
                cells.AddRange(graph.Diagnostics.GetAllRegionCenters());
            return ConvexHull(cells);
        }

        /// <summary>2D (XZ) convex hull (monotone chain), returned as an ordered loop.</summary>
        private static List<Vector3> ConvexHull(List<Vector3> points)
        {
            if (points == null || points.Count < 3)
                return points ?? new List<Vector3>();

            var pts = new List<Vector3>(points);
            pts.Sort((a, b) =>
                Mathf.Approximately(a.x, b.x) ? a.z.CompareTo(b.z) : a.x.CompareTo(b.x));

            var hull = new List<Vector3>();
            foreach (var p in pts)
            {
                while (hull.Count >= 2 &&
                       Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            var lower = hull.Count + 1;
            for (var i = pts.Count - 2; i >= 0; i--)
            {
                var p = pts[i];
                while (hull.Count >= lower &&
                       Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        private static float Cross(Vector3 o, Vector3 a, Vector3 b)
        {
            return (a.x - o.x) * (b.z - o.z) - (a.z - o.z) * (b.x - o.x);
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
