using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Behaviors.Farming
{
    /// <summary>
    ///     Handles finding valid planting positions on cultivated ground and
    ///     instantiating plant piece prefabs for NPC farming.
    /// </summary>
    public static class PlantingHelper
    {
        private static int s_spaceMask;

        private static readonly Collider[] s_overlapBuffer = new Collider[128];

        /// <summary>
        ///     Find a valid planting position near the given center on cultivated ground.
        ///     Mirrors Valheim's Plant.HaveGrowSpace: any collider within growRadius
        ///     on the space-check layers blocks the position (unless it's a healthy Plant,
        ///     which uses growRadius * 2 spacing instead).
        /// </summary>
        public static Vector3? FindPlantingPosition(
            Vector3 center, float searchRadius, float growRadius)
        {
            EnsureSpaceMask();
            var plantSpacing = Mathf.Max(growRadius * 2f, 1.5f);

            // Resolve the village shell once so we never pick a spot outside the walls — a
            // spot past the palisade strands the farmer trying to path to it. A null /
            // unclassified graph means "don't filter" (never block farming where we can't
            // classify the hull).
            var shellGraph = VillageRegistry.GraphAt(center);

            DebugLog.Append("PlantingHelper.cs:FindPlantingPosition", "Spacing check start",
                new Dictionary<string, object>
                {
                    { "plantSpacing", plantSpacing }, { "growRadius", growRadius }, { "searchRadius", searchRadius },
                    { "center", center.ToString() },
                }, "H3", "run1");

            for (var r = 0f; r <= searchRadius; r += plantSpacing)
            {
                var steps = r < 0.1f ? 1 : Mathf.Max(4, Mathf.RoundToInt(2f * Mathf.PI * r / plantSpacing));
                for (var i = 0; i < steps; i++)
                {
                    var angle = 2f * Mathf.PI * i / steps;
                    var candidate = center + new Vector3(
                        Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);

                    if (!GetTerrainHeight(candidate, out var height))
                        continue;
                    candidate.y = height;

                    // Plant only inside the village shell (boundary cells included);
                    // reject outside-the-wall cultivated ground.
                    if (shellGraph != null && shellGraph.HasClassification &&
                        VillageShell.IsOutside(shellGraph, candidate))
                        continue;

                    if (!IsCultivated(candidate))
                        continue;

                    if (!HasGrowSpace(candidate, growRadius, plantSpacing))
                        continue;

                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        ///     Place a plant piece prefab at the given position.
        ///     Returns the instantiated GameObject, or null on failure.
        /// </summary>
        public static GameObject PlacePlant(GameObject piecePrefab, Vector3 position)
        {
            if (piecePrefab == null) return null;

            var rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var go = Object.Instantiate(piecePrefab, position, rotation);
            if (go == null) return null;

            var piece = go.GetComponent<Piece>();
            if (piece != null)
                piece.SetCreator(0L);

            Plugin.Log?.LogInfo(
                $"[Farming] Planted {piecePrefab.name} at {position}");
            return go;
        }

        public static bool IsCultivated(Vector3 worldPos)
        {
            var heightmap = Heightmap.FindHeightmap(worldPos);
            return heightmap != null && heightmap.IsCultivated(worldPos);
        }

        private static bool GetTerrainHeight(Vector3 worldPos, out float height)
        {
            height = 0f;
            if (ZoneSystem.instance == null) return false;
            height = ZoneSystem.instance.GetGroundHeight(worldPos);
            return height > -1000f;
        }

        /// <summary>
        ///     Two-pass grow-space check:
        ///     1. Valheim-style obstruction: any non-Plant collider within growRadius blocks.
        ///     2. Plant-to-plant spacing: any Plant within plantSpacing blocks.
        /// </summary>
        private static bool HasGrowSpace(
            Vector3 candidate, float growRadius, float plantSpacing)
        {
            var checkRadius = Mathf.Max(growRadius, plantSpacing);
            var count = Physics.OverlapSphereNonAlloc(
                candidate, checkRadius, s_overlapBuffer, s_spaceMask);

            var growRadiusSq = growRadius * growRadius;
            var plantSpacingSq = plantSpacing * plantSpacing;

            for (var i = 0; i < count; i++)
            {
                var col = s_overlapBuffer[i];
                if (col == null) continue;

                var plant = col.GetComponentInParent<Plant>();
                if (plant != null)
                {
                    var dx = candidate.x - plant.transform.position.x;
                    var dz = candidate.z - plant.transform.position.z;
                    if (dx * dx + dz * dz < plantSpacingSq)
                        return false;
                    continue;
                }

                var pickable = col.GetComponentInParent<Pickable>();
                if (pickable != null)
                {
                    var dx = candidate.x - pickable.transform.position.x;
                    var dz = candidate.z - pickable.transform.position.z;
                    if (dx * dx + dz * dz < plantSpacingSq)
                        return false;
                    continue;
                }

                var distSq = (candidate - col.ClosestPoint(candidate)).sqrMagnitude;
                if (distSq < growRadiusSq)
                    return false;
            }

            return true;
        }

        private static void EnsureSpaceMask()
        {
            if (s_spaceMask != 0) return;
            s_spaceMask = LayerMask.GetMask(
                "Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
        }
    }
}