using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Static manager for all active village areas. Areas are published by
    ///     <see cref="RefreshFromVillage" /> the moment HNA partitioning completes,
    ///     independent of any patroller. Spawn protection and enemy avoidance patches
    ///     query this manager.
    /// </summary>
    public static class VillageAreaManager
    {
        private static readonly Dictionary<string, VillageArea> s_areas = new();

        /// <summary>
        ///     Get the number of registered village areas.
        /// </summary>
        public static int AreaCount => s_areas.Count;

        /// <summary>Read-only enumeration of all registered village areas.</summary>
        public static IEnumerable<VillageArea> AllAreas => s_areas.Values;

        /// <summary>
        ///     Register a village area (keyed by HNA village key).
        ///     Replaces any existing area for the same village.
        /// </summary>
        public static void RegisterArea(VillageArea area)
        {
            if (area == null) return;
            s_areas[area.VillageId] = area;
            Plugin.Log?.LogInfo(
                $"[VillageArea] Registered area for {area.VillageId} with {area.Waypoints.Count} waypoints");
            VillageStationRegistry.RefreshFor(area);
            VillagePoiRegistry.RefreshFor(area);
        }

        /// <summary>
        ///     Remove a village area by village id.
        /// </summary>
        public static void UnregisterArea(string villageId)
        {
            if (s_areas.Remove(villageId))
            {
                Plugin.Log?.LogInfo($"[VillageArea] Unregistered area for {villageId}");
                VillageStationRegistry.RemoveFor(villageId);
                VillagePoiRegistry.RemoveFor(villageId);
            }
        }

        /// <summary>
        ///     Build (or replace) the VillageArea for the given village using its graph's
        ///     boundary cells. Called by RegionPartitionHandler after a partition completes —
        ///     the area exists the moment HNA finishes, independent of any patroller.
        /// </summary>
        public static void RefreshFromVillage(Village village)
        {
            if (village == null || !village.HasGraph) return;

            var id = village.VillageId;
            var waypoints = PatrolRouteBuilder.Build(village.Graph.GetBoundaryCells());
            if (waypoints == null || waypoints.Count < 3)
            {
                // No real boundary (e.g. a registry with no enclosing/connected build pieces).
                // Fall back to a circular area the size of the nearest workbench's build range,
                // centered on the registry. Auto-heals: the next partition that finds a real
                // boundary replaces this circle.
                var fallback = TryBuildWorkbenchRadiusArea(village);
                if (fallback != null)
                {
                    RegisterArea(fallback);
                    return;
                }

                Plugin.Log?.LogInfo(
                    $"[VillageArea] Skipped registration for village={id}: insufficient boundary " +
                    $"waypoints ({waypoints?.Count ?? 0}) and no nearby workbench to size a fallback");
                return;
            }

            RegisterArea(new VillageArea(id, waypoints));
        }

        /// <summary>
        ///     Fallback village area used when no real boundary can be derived: a circle the size
        ///     of the nearest workbench's build range, centered on the registry. Returns null if
        ///     there's no workbench nearby to size it against (we don't invent a radius).
        /// </summary>
        private static VillageArea TryBuildWorkbenchRadiusArea(Village village)
        {
            const float searchRange = 64f; // generous: the registry is normally built within a workbench's range
            const int segments = 16;

            var center = village.Anchor; // registry placement position
            var workbench = CraftingStation.FindClosestStationInRange("$piece_workbench", center, searchRange);
            if (workbench == null) return null;

            var radius = workbench.GetStationBuildRange();
            if (radius <= 0f) return null;

            Plugin.Log?.LogInfo(
                $"[VillageArea] village={village.VillageId}: no boundary found — using circular fallback " +
                $"radius={radius:F1}m (nearest workbench) centered on the registry");

            return new VillageArea(village.VillageId, BuildCircleWaypoints(center, radius, segments));
        }

        /// <summary>A closed ring of <paramref name="segments" /> evenly spaced points on a circle (XZ plane).</summary>
        private static List<Vector3> BuildCircleWaypoints(Vector3 center, float radius, int segments)
        {
            var points = new List<Vector3>(segments);
            for (var i = 0; i < segments; i++)
            {
                var angle = 2f * Mathf.PI * i / segments;
                points.Add(new Vector3(
                    center.x + radius * Mathf.Cos(angle), center.y, center.z + radius * Mathf.Sin(angle)));
            }

            return points;
        }

        /// <summary>
        ///     Check if a position is inside any registered village area.
        ///     Used by spawn protection to suppress enemy spawns.
        /// </summary>
        public static bool IsInsideAnyVillage(Vector3 position)
        {
            foreach (var area in s_areas.Values)
                if (area.IsInsideArea(position))
                    return true;
            return false;
        }

        /// <summary>
        ///     Check if a position is within a given radius of any village boundary.
        ///     Used by enemy avoidance to keep mobs away from village edges.
        /// </summary>
        public static bool IsNearAnyVillage(Vector3 position, float radius)
        {
            foreach (var area in s_areas.Values)
                if (area.IsInsideArea(position) || area.IsNearBoundary(position, radius))
                    return true;
            return false;
        }

        /// <summary>
        ///     Axis-aligned XZ bounds of ONE village's area (its patrol ring). Returns false
        ///     when that village has no registered area yet.
        ///     <para>
        ///         This was <c>TryGetCombinedBounds</c>, which unioned EVERY registered area.
        ///         Its only caller seeds the partition's bake/footprint box with it, so in a
        ///         world with two villages every partition started from a box spanning both.
        ///         Measured at 300m apart: the box wanted 61,892 cells against the 40,000 cap,
        ///         and <c>ExpandFootprintToVillagePieces</c>' area clamp then shrank it about
        ///         the village's own centroid — so the published footprint claimed ~250m of the
        ///         other village's empty ground while cutting 14m of this village's own build
        ///         out of it, and the graph baked over that box left the village's own
        ///         ingredient chest outside every region. Bounds must be per-village for the
        ///         same reason anchors are (<c>RegionPartitionHandler.FilterAnchorsByTask</c>).
        ///     </para>
        /// </summary>
        public static bool TryGetAreaBounds(
            string villageId, out float minX, out float minZ, out float maxX, out float maxZ)
        {
            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            if (string.IsNullOrEmpty(villageId) || !s_areas.TryGetValue(villageId, out var area)) return false;

            foreach (var wp in area.Waypoints)
            {
                if (wp.x < minX) minX = wp.x;
                if (wp.x > maxX) maxX = wp.x;
                if (wp.z < minZ) minZ = wp.z;
                if (wp.z > maxZ) maxZ = wp.z;
            }

            return minX <= maxX && minZ <= maxZ;
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_areas.Clear();
            VillageStationRegistry.Clear();
            VillagePoiRegistry.Clear();
        }
    }
}