using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Algorithms
{
    /// <summary>A discovered region cell with its world position.</summary>
    public class RegionCell
    {
        public int Ix { get; }
        public int Iz { get; }
        public float Wx { get; }
        public float Wz { get; }
        public float Y { get; }

        public RegionCell(int ix, int iz, float wx, float wz, float y)
        {
            Ix = ix; Iz = iz; Wx = wx; Wz = wz; Y = y;
        }
    }

    /// <summary>A blocking barrier in XZ plane (used for both doors and wall pieces).</summary>
    public struct Barrier
    {
        public float Px, Pz, Py;       // position
        public float Fx, Fz;           // forward direction (blocking normal, normalized in XZ)
        public float HalfWidth2;       // squared half-width perpendicular to forward
        public float YTolerance;       // max Y distance for this barrier to apply (0 = ignore Y)
    }

    /// <summary>
    /// Delegate for height lookups during flood fill. Allows the algorithm to remain
    /// independent of Unity raycasts or offline height providers.
    /// </summary>
    /// <param name="wx">World X position.</param>
    /// <param name="wz">World Z position.</param>
    /// <param name="refY">Reference Y (parent cell height).</param>
    /// <param name="hitY">Output hit height.</param>
    /// <returns>True if a valid height was found.</returns>
    public delegate bool HeightLookup(float wx, float wz, float refY, out float hitY);

    /// <summary>
    /// Pure BFS flood-fill algorithm for HNA region discovery.
    /// Decoupled from Unity/test-specific data structures via HeightLookup delegate.
    /// </summary>
    public static class FloodFill
    {
        public const float MaxWalkableSlopeDeg = 26f;
        public const float FloodFillRadius = 25f;
        public const float DoorBlockRadius = 4f;
        public const float WallHalfWidth = 2.5f;
        public const float WallYTolerance = 3f;

        /// <summary>
        /// Returns true if the line from (ax,az)→(bx,bz) at height cellY crosses any barrier
        /// within its blocking radius and Y tolerance.
        /// </summary>
        public static bool CrossesBarrier(float ax, float az, float bx, float bz,
            float cellY, List<Barrier> barriers)
        {
            for (int i = 0; i < barriers.Count; i++)
            {
                var b = barriers[i];
                if (b.YTolerance > 0 && Math.Abs(cellY - b.Py) > b.YTolerance) continue;

                float dA = (ax - b.Px) * b.Fx + (az - b.Pz) * b.Fz;
                float dB = (bx - b.Px) * b.Fx + (bz - b.Pz) * b.Fz;
                if (dA * dB > 0) continue;
                float denom = dA - dB;
                if (Math.Abs(denom) < 0.001f) continue;
                float t = dA / denom;
                float cx = ax + t * (bx - ax);
                float cz = az + t * (bz - az);
                float dist2 = (cx - b.Px) * (cx - b.Px) + (cz - b.Pz) * (cz - b.Pz);
                if (dist2 <= b.HalfWidth2) return true;
            }
            return false;
        }

        /// <summary>
        /// Run BFS flood fill from bed positions.
        /// </summary>
        /// <param name="beds">Bed positions as Vector3 array.</param>
        /// <param name="barriers">Pre-built barrier list.</param>
        /// <param name="heightLookup">Height lookup delegate.</param>
        /// <param name="originX">Grid origin X.</param>
        /// <param name="originZ">Grid origin Z.</param>
        /// <param name="cellSize">Grid cell size.</param>
        /// <param name="cellCountX">Grid width in cells.</param>
        /// <param name="cellCountZ">Grid depth in cells.</param>
        /// <returns>List of discovered region cells.</returns>
        public static List<RegionCell> Run(
            Vector3[] beds,
            List<Barrier> barriers,
            HeightLookup heightLookup,
            float originX, float originZ,
            float cellSize,
            int cellCountX, int cellCountZ)
        {
            if (beds.Length == 0) return new List<RegionCell>();

            var regionIds = new HashSet<string>();
            var cellPositions = new Dictionary<string, RegionCell>();
            var queue = new Queue<string>();
            float r2 = FloodFillRadius * FloodFillRadius;

            // Seed from beds
            foreach (var bed in beds)
            {
                int ix = (int)Math.Floor((bed.x - originX) / cellSize);
                int iz = (int)Math.Floor((bed.z - originZ) / cellSize);
                if (ix < 0 || ix >= cellCountX || iz < 0 || iz >= cellCountZ)
                    continue;

                string id = ix + "_" + iz;
                float wx = originX + (ix + 0.5f) * cellSize;
                float wz = originZ + (iz + 0.5f) * cellSize;

                float hitY;
                if (!heightLookup(wx, wz, bed.y, out hitY))
                    hitY = bed.y;

                var cell = new RegionCell(ix, iz, wx, wz, hitY);
                if (regionIds.Add(id))
                {
                    cellPositions[id] = cell;
                    queue.Enqueue(id);
                }
            }

            // BFS
            while (queue.Count > 0)
            {
                string fromId = queue.Dequeue();
                RegionCell posA;
                if (!cellPositions.TryGetValue(fromId, out posA)) continue;

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        if (dx == 0 && dz == 0) continue;
                        int nx = posA.Ix + dx, nz = posA.Iz + dz;
                        if (nx < 0 || nx >= cellCountX || nz < 0 || nz >= cellCountZ) continue;
                        string toId = nx + "_" + nz;
                        if (regionIds.Contains(toId)) continue;

                        float wx = originX + (nx + 0.5f) * cellSize;
                        float wz = originZ + (nz + 0.5f) * cellSize;

                        // Distance limit from any bed
                        bool nearBed = false;
                        foreach (var bed in beds)
                        {
                            float bdx = wx - bed.x, bdz = wz - bed.z;
                            if (bdx * bdx + bdz * bdz <= r2) { nearBed = true; break; }
                        }
                        if (!nearBed) continue;

                        // Height lookup using parent's Y as reference
                        float hitY;
                        if (!heightLookup(wx, wz, posA.Y, out hitY))
                            continue;

                        // Slope check
                        float dxzLen = (float)Math.Sqrt(
                            (wx - posA.Wx) * (wx - posA.Wx) + (wz - posA.Wz) * (wz - posA.Wz));
                        if (dxzLen < 0.01f) continue;
                        float dy = Math.Abs(hitY - posA.Y);
                        float slopeDeg = (float)Math.Atan2(dy, dxzLen) * (180f / (float)Math.PI);
                        if (slopeDeg > MaxWalkableSlopeDeg)
                            continue;

                        // Barrier check
                        if (CrossesBarrier(posA.Wx, posA.Wz, wx, wz, posA.Y, barriers))
                            continue;

                        var cell = new RegionCell(nx, nz, wx, wz, hitY);
                        regionIds.Add(toId);
                        cellPositions[toId] = cell;
                        queue.Enqueue(toId);
                    }
                }
            }

            var result = new List<RegionCell>(cellPositions.Count);
            foreach (var kvp in cellPositions)
                result.Add(kvp.Value);
            return result;
        }
    }
}
