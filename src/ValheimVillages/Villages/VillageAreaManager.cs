using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villages
{
    /// <summary>
    /// Static manager for all active village areas.
    /// Patrollers register their patrol polygons here after completing a circuit.
    /// Spawn protection and enemy avoidance patches query this manager.
    /// </summary>
    public static class VillageAreaManager
    {
        private static readonly Dictionary<string, VillageArea> s_areas = new();

        /// <summary>
        /// Register a village area (keyed by patroller's unique ID).
        /// Replaces any existing area for the same patroller.
        /// </summary>
        public static void RegisterArea(VillageArea area)
        {
            if (area == null) return;
            s_areas[area.PatrollerId] = area;
            Plugin.Log?.LogInfo($"[VillageArea] Registered area for patroller {area.PatrollerId} with {area.Waypoints.Count} waypoints");
        }

        /// <summary>
        /// Remove a village area by patroller ID.
        /// </summary>
        public static void UnregisterArea(string patrollerId)
        {
            if (s_areas.Remove(patrollerId))
                Plugin.Log?.LogInfo($"[VillageArea] Unregistered area for patroller {patrollerId}");
        }

        /// <summary>
        /// Check if a position is inside any registered village area.
        /// Used by spawn protection to suppress enemy spawns.
        /// </summary>
        public static bool IsInsideAnyVillage(Vector3 position)
        {
            foreach (var area in s_areas.Values)
            {
                if (area.IsInsideArea(position))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a position is within a given radius of any village boundary.
        /// Used by enemy avoidance to keep mobs away from village edges.
        /// </summary>
        public static bool IsNearAnyVillage(Vector3 position, float radius)
        {
            foreach (var area in s_areas.Values)
            {
                if (area.IsInsideArea(position) || area.IsNearBoundary(position, radius))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a position is INSIDE any registered village area AND at
        /// least <paramref name="shrinkMargin"/> meters away from the polygon
        /// boundary. Used to virtually shrink the polygon inward without
        /// modifying its vertices, e.g. to exclude positions where the patrol
        /// path wrapped slightly outside the actual wall line.
        /// </summary>
        public static bool IsInsideDeepAnyVillage(Vector3 position, float shrinkMargin)
        {
            foreach (var area in s_areas.Values)
            {
                if (area.IsInsideArea(position) && !area.IsNearBoundary(position, shrinkMargin))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Get the number of registered village areas.
        /// </summary>
        public static int AreaCount => s_areas.Count;

        /// <summary>
        /// Get the combined axis-aligned XZ bounds of all registered village areas.
        /// Returns false if there are no areas.
        /// </summary>
        public static bool TryGetCombinedBounds(out float minX, out float minZ, out float maxX, out float maxZ)
        {
            minX = minZ = float.MaxValue;
            maxX = maxZ = float.MinValue;
            if (s_areas.Count == 0) return false;
            foreach (var area in s_areas.Values)
            {
                foreach (var wp in area.Waypoints)
                {
                    if (wp.x < minX) minX = wp.x;
                    if (wp.x > maxX) maxX = wp.x;
                    if (wp.z < minZ) minZ = wp.z;
                    if (wp.z > maxZ) maxZ = wp.z;
                }
            }
            return minX <= maxX && minZ <= maxZ;
        }

        /// <summary>
        /// Clear all registered areas (e.g. on world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            s_areas.Clear();
        }
    }
}
