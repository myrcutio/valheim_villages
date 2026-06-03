using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Renders the minimal per-task map for the Tasks tab: the village
    ///     perimeter (patrol route or a convex hull of the walkable region cells)
    ///     plus task pins (work-order chest, player).
    /// </summary>
    public static class VillageMapPanel
    {
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
    }
}
