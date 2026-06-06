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
            VillageRoomCatalog.RefreshFor(area);
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
                VillageRoomCatalog.RemoveFor(villageId);
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
                Plugin.Log?.LogInfo(
                    $"[VillageArea] Skipped registration for village={id}: insufficient boundary waypoints ({waypoints?.Count ?? 0})");
                return;
            }

            RegisterArea(new VillageArea(id, waypoints));
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

        [RegisterCleanup]
        public static void Clear()
        {
            s_areas.Clear();
            VillageStationRegistry.Clear();
            VillagePoiRegistry.Clear();
            VillageRoomCatalog.Clear();
        }
    }
}