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
        // Detected gate/door markers on the village map. Cyan reads clearly
        // against the tan perimeter and the warmer task pins.
        private static readonly Color GatePinColor = new(0.25f, 0.85f, 0.95f, 1f);

        /// <summary>
        ///     Render a per-task map for a villager, with optional extra pins for task-relevant locations.
        ///     Returns null if no useful map can be drawn.
        /// </summary>
        public static Texture2D RenderForTask(
            VillagerBehaviorBridge villager,
            IReadOnlyList<(Vector3 position, Color color)> pins)
        {
            if (villager == null) return null;
            return PatrolMapRenderer.RenderMinimal(
                GetPerimeter(villager), WithGatePins(villager, pins));
        }

        /// <summary>
        ///     Append a pin for every gate the partition sealed into this
        ///     villager's village boundary, so detected gates are visible on
        ///     the map alongside the task pins.
        /// </summary>
        private static IReadOnlyList<(Vector3 position, Color color)> WithGatePins(
            VillagerBehaviorBridge villager,
            IReadOnlyList<(Vector3 position, Color color)> pins)
        {
            var bed = villager.AI?.BedPosition ?? Vector3.zero;
            var graph = Villages.Entity.VillageRegistry.GraphAt(bed);
            var gates = graph?.GetGates();
            if (gates == null || gates.Count == 0) return pins;

            var merged = new List<(Vector3 position, Color color)>();
            if (pins != null) merged.AddRange(pins);
            foreach (var g in gates) merged.Add((g, GatePinColor));
            return merged;
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

            // Non-patrollers (e.g. the Farmer) have no route. Outline the
            // village by its boundary cells — the outer ring, where the gate
            // pins sit — so the shape encloses the gates. The convex hull of
            // region CENTERS used previously is inset toward the middle, so
            // adding gate pins at the wall ring blew the map bounds out and
            // left the outline as a tiny shape floating in the centre.
            var bed = villager.AI?.BedPosition ?? Vector3.zero;
            var graph = Villages.Entity.VillageRegistry.GraphAt(bed);
            if (graph != null)
            {
                var boundary = graph.GetBoundaryCells();
                if (boundary.Count >= 3)
                {
                    var pts = new List<Vector3>(boundary.Count);
                    foreach (var b in boundary) pts.Add(b.worldCenter);
                    return ConvexHull(pts);
                }
            }

            var cells = new List<Vector3>();
            foreach (var g in Villages.Entity.VillageRegistry.AllGraphs())
                cells.AddRange(g.Diagnostics.GetAllRegionCenters());
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
