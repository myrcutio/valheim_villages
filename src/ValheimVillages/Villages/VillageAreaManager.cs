using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Static manager for all active village areas. Areas are published by
    ///     <see cref="RefreshFromRegionGraph" /> the moment HNA partitioning completes,
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
            s_areas[area.VillageKey] = area;
            Plugin.Log?.LogInfo(
                $"[VillageArea] Registered area for {area.VillageKey} with {area.Waypoints.Count} waypoints");
            VillageStationRegistry.RefreshFor(area);
            VillagePoiRegistry.RefreshFor(area);
            VillageRoomCatalog.RefreshFor(area);
        }

        /// <summary>
        ///     Remove a village area by village key.
        /// </summary>
        public static void UnregisterArea(string villageKey)
        {
            if (s_areas.Remove(villageKey))
            {
                Plugin.Log?.LogInfo($"[VillageArea] Unregistered area for {villageKey}");
                VillageStationRegistry.RemoveFor(villageKey);
                VillagePoiRegistry.RemoveFor(villageKey);
                VillageRoomCatalog.RemoveFor(villageKey);
            }
        }

        /// <summary>
        ///     Build (or replace) the VillageArea for the given region graph using its boundary cells.
        ///     Called by RegionPartitionHandler after a partition completes — the village exists the moment
        ///     HNA finishes, independent of any patroller.
        /// </summary>
        public static void RefreshFromRegionGraph(RegionGraph graph)
        {
            if (graph == null || !graph.IsAvailable) return;

            var key = graph.RegisteredVillageKey;
            if (string.IsNullOrEmpty(key)) return;

            var waypoints = BoundaryMapper.ComputeBoundaryWaypoints(graph);
            if (waypoints == null || waypoints.Count < 3)
            {
                Plugin.Log?.LogInfo(
                    $"[VillageArea] Skipped registration for key={key}: insufficient boundary waypoints ({waypoints?.Count ?? 0})");
                return;
            }

            RegisterArea(new VillageArea(key, waypoints));
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
        ///     Get the combined axis-aligned XZ bounds of all registered village areas.
        ///     Returns false if there are no areas.
        /// </summary>
        public static bool TryGetCombinedBounds(out float minX, out float minZ, out float maxX, out float maxZ)
        {
            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            if (s_areas.Count == 0) return false;
            foreach (var area in s_areas.Values)
            foreach (var wp in area.Waypoints)
            {
                if (wp.x < minX) minX = wp.x;
                if (wp.x > maxX) maxX = wp.x;
                if (wp.z < minZ) minZ = wp.z;
                if (wp.z > maxZ) maxZ = wp.z;
            }

            return minX <= maxX && minZ <= maxZ;
        }

        /// <summary>
        ///     Clear all registered areas (e.g. on world unload).
        /// </summary>
        /// <summary>
        ///     Find the village whose polygon contains the position. When several
        ///     overlap, return the smallest (most specific) polygon's key. Shared
        ///     by the station and PoI registries so they agree on membership.
        /// </summary>
        public static bool TryGetContainingVillageKey(Vector3 position, out string villageKey)
        {
            villageKey = null;
            string best = null;
            var bestSizeSq = float.MaxValue;

            foreach (var area in s_areas.Values)
            {
                if (area == null || !area.IsInsideArea(position)) continue;
                float minX = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var wp in area.Waypoints)
                {
                    if (wp.x < minX) minX = wp.x;
                    if (wp.x > maxX) maxX = wp.x;
                    if (wp.z < minZ) minZ = wp.z;
                    if (wp.z > maxZ) maxZ = wp.z;
                }

                var sizeSq = (maxX - minX) * (maxZ - minZ);
                if (sizeSq < bestSizeSq)
                {
                    bestSizeSq = sizeSq;
                    best = area.VillageKey;
                }
            }

            villageKey = best;
            return best != null;
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