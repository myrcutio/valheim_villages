using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Core.Attributes;

namespace ValheimVillages.Villages
{
    /// <summary>
    /// Static manager for all active village areas.
    /// Guards register their patrol polygons here after completing a circuit.
    /// Spawn protection and enemy avoidance patches query this manager.
    /// </summary>
    public static class VillageAreaManager
    {
        private static readonly Dictionary<string, VillageArea> s_areas = new();

        /// <summary>
        /// Register a village area (keyed by guard's unique ID).
        /// Replaces any existing area for the same guard.
        /// </summary>
        public static void RegisterArea(VillageArea area)
        {
            if (area == null) return;
            s_areas[area.GuardId] = area;
            Plugin.Log?.LogInfo($"[VillageArea] Registered area for guard {area.GuardId} with {area.Waypoints.Count} waypoints");
        }

        /// <summary>
        /// Remove a village area by guard ID.
        /// </summary>
        public static void UnregisterArea(string guardId)
        {
            if (s_areas.Remove(guardId))
                Plugin.Log?.LogInfo($"[VillageArea] Unregistered area for guard {guardId}");
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
