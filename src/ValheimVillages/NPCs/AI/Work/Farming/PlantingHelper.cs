using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work.Farming
{
    /// <summary>
    /// Handles finding valid planting positions on cultivated ground and
    /// instantiating plant piece prefabs for NPC farming.
    /// </summary>
    public static class PlantingHelper
    {
        /// <summary>Radius to scan for existing plants when checking spacing.</summary>
        private const float PlantScanRadius = 20f;

        /// <summary>
        /// Find a valid planting position near the given center on cultivated ground.
        /// Searches in a grid pattern, checking cultivated ground and plant spacing.
        /// Returns null if no valid position is found.
        /// </summary>
        public static Vector3? FindPlantingPosition(
            Vector3 center, float searchRadius, float growRadius)
        {
            // Collect existing plant positions to check spacing
            var existingPlants = GetNearbyPlantPositions(center, PlantScanRadius);
            float spacing = Mathf.Max(growRadius * 2f, 1.5f);

            // Search in a spiral-like grid pattern from center outward
            for (float r = 0f; r <= searchRadius; r += spacing)
            {
                int steps = r < 0.1f ? 1 : Mathf.Max(4, Mathf.RoundToInt(2f * Mathf.PI * r / spacing));
                for (int i = 0; i < steps; i++)
                {
                    float angle = (2f * Mathf.PI * i) / steps;
                    var candidate = center + new Vector3(
                        Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);

                    // Snap to terrain height
                    if (!GetTerrainHeight(candidate, out float height))
                        continue;
                    candidate.y = height;

                    if (!IsCultivated(candidate))
                        continue;

                    if (!HasEnoughSpacing(candidate, existingPlants, spacing))
                        continue;

                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Place a plant piece prefab at the given position.
        /// Returns the instantiated GameObject, or null on failure.
        /// </summary>
        public static GameObject PlacePlant(GameObject piecePrefab, Vector3 position)
        {
            if (piecePrefab == null) return null;

            var rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var go = Object.Instantiate(piecePrefab, position, rotation);
            if (go == null) return null;

            // Set the piece creator (0 = no specific player)
            var piece = go.GetComponent<Piece>();
            if (piece != null)
                piece.SetCreator(0L);

            Plugin.Log?.LogInfo(
                $"[Farming] Planted {piecePrefab.name} at {position}");
            return go;
        }

        /// <summary>
        /// Check if the given world position is on cultivated ground.
        /// </summary>
        public static bool IsCultivated(Vector3 worldPos)
        {
            var heightmap = Heightmap.FindHeightmap(worldPos);
            return heightmap != null && heightmap.IsCultivated(worldPos);
        }

        /// <summary>
        /// Get the terrain height at a world position.
        /// </summary>
        private static bool GetTerrainHeight(Vector3 worldPos, out float height)
        {
            height = 0f;
            if (ZoneSystem.instance == null) return false;
            height = ZoneSystem.instance.GetGroundHeight(worldPos);
            return height > -1000f; // Valid terrain
        }

        /// <summary>
        /// Get positions of all Plant components near a center point.
        /// </summary>
        private static List<Vector3> GetNearbyPlantPositions(Vector3 center, float radius)
        {
            var positions = new List<Vector3>();
            var colliders = Physics.OverlapSphere(center, radius);
            var seen = new HashSet<int>();
            foreach (var col in colliders)
            {
                if (col == null) continue;
                var plant = col.GetComponentInParent<Plant>();
                if (plant == null) continue;
                int id = plant.GetInstanceID();
                if (!seen.Add(id)) continue;
                positions.Add(plant.transform.position);
            }
            return positions;
        }

        /// <summary>
        /// Check that a candidate position has enough spacing from all existing plants.
        /// </summary>
        private static bool HasEnoughSpacing(
            Vector3 candidate, List<Vector3> existingPlants, float minSpacing)
        {
            float minSq = minSpacing * minSpacing;
            foreach (var pos in existingPlants)
            {
                float dx = candidate.x - pos.x;
                float dz = candidate.z - pos.z;
                if (dx * dx + dz * dz < minSq)
                    return false;
            }
            return true;
        }
    }
}
