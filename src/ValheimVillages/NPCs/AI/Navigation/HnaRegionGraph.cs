using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Core.Attributes;
using ValheimVillages.Villages;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Link type between two HNA regions (off-mesh connection).
    /// </summary>
    public enum HnaLinkType
    {
        Door,
        Stair,
        Slope
    }

    /// <summary>
    /// A single link between two regions (door or stair).
    /// </summary>
    public struct HnaLink
    {
        public string FromRegionId;
        public string ToRegionId;
        public HnaLinkType LinkType;
        public Vector3 PositionStart;
        public Vector3 PositionEnd;
    }

    /// <summary>
    /// Hierarchical region graph for HNA*-style pathfinding.
    /// Regions are coarse grid cells inside village (non-spawnable) areas;
    /// links are doors and stairs connecting regions.
    /// Built by the low-priority hna_partition task.
    /// </summary>
    public static class HnaRegionGraph
    {
        /// <summary>Grid cell size in meters (XZ). 3m balances resolution with smoothness —
        /// finer than 4m for capturing corners, coarser than 2m to avoid grid noise.</summary>
        public const float CellSize = 3f;

        /// <summary>Get solid height at (worldX, worldZ). Tries from above then from below and uses the higher result so modified/cultivated terrain (top layer) is used consistently and we avoid stacked regions.</summary>
        public static bool GetSolidHeightAt(float worldX, float worldZ, out float height)
        {
            height = 0f;
            if (ZoneSystem.instance == null) return false;
            bool fromAbove = ZoneSystem.instance.GetSolidHeight(new Vector3(worldX, 500f, worldZ), out float hAbove, 550);
            bool fromBelow = ZoneSystem.instance.GetSolidHeight(new Vector3(worldX, 0f, worldZ), out float hBelow, 500);
            if (fromAbove && fromBelow)
            {
                height = Mathf.Max(hAbove, hBelow);
                return true;
            }
            if (fromAbove) { height = hAbove; return true; }
            if (fromBelow) { height = hBelow; return true; }
            return false;
        }

        private static float s_originX;
        private static float s_originZ;
        private static readonly HashSet<string> s_regionIds = new HashSet<string>();
        private static readonly List<HnaLink> s_links = new List<HnaLink>();
        private static Dictionary<string, string> s_cellToRegion;
        /// <summary>BFS-discovered floor heights per cell (cell id → Y). Used for accurate marker placement.</summary>
        private static readonly Dictionary<string, float> s_cellHeights = new Dictionary<string, float>();
        private static bool s_initialized;

        /// <summary>True if the graph has been built and has at least one region.</summary>
        public static bool IsAvailable => s_initialized && s_regionIds.Count > 0;

        /// <summary>Number of regions in the graph.</summary>
        public static int RegionCount => s_regionIds.Count;

        /// <summary>Number of links (doors + stairs).</summary>
        public static int LinkCount => s_links.Count;

        /// <summary>
        /// Get the region id for a world position (XZ). Returns null if outside the grid or not a valid region.
        /// When merging is used, maps cell to merged region id.
        /// </summary>
        public static string PointToRegionId(Vector3 worldPosition)
        {
            if (!s_initialized) return null;
            int ix = Mathf.FloorToInt((worldPosition.x - s_originX) / CellSize);
            int iz = Mathf.FloorToInt((worldPosition.z - s_originZ) / CellSize);
            int hb = HeightBucket(worldPosition.y);
            // Search nearby height buckets to find the closest matching region
            for (int d = 0; d <= 3; d++)
            {
                string k1 = $"{ix}_{iz}_h{hb + d}";
                if (s_cellToRegion != null && s_cellToRegion.TryGetValue(k1, out string r1))
                    return s_regionIds.Contains(r1) ? r1 : null;
                if (s_regionIds.Contains(k1)) return k1;
                if (d > 0)
                {
                    string k2 = $"{ix}_{iz}_h{hb - d}";
                    if (s_cellToRegion != null && s_cellToRegion.TryGetValue(k2, out string r2))
                        return s_regionIds.Contains(r2) ? r2 : null;
                    if (s_regionIds.Contains(k2)) return k2;
                }
            }
            return null;
        }

        /// <summary>
        /// Check if a region id is valid.
        /// </summary>
        public static bool IsValidRegion(string regionId) =>
            s_initialized && !string.IsNullOrEmpty(regionId) && s_regionIds.Contains(regionId);

        /// <summary>Get the BFS-discovered floor height for a cell. Returns false if no stored height.</summary>
        public static bool TryGetCellHeight(string cellId, out float height)
        {
            height = 0f;
            return s_initialized && cellId != null && s_cellHeights.TryGetValue(cellId, out height);
        }

        /// <summary>Get the grid origin (minX, minZ) used for partitioning. Returns false if graph not built.</summary>
        public static bool GetOrigin(out float originX, out float originZ)
        {
            originX = s_originX;
            originZ = s_originZ;
            return s_initialized;
        }

        /// <summary>Get the XZ bounds of a region cell in world space. Returns false if region invalid.</summary>
        public static bool GetRegionBounds(string regionId, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            if (!s_initialized || string.IsNullOrEmpty(regionId) || !TryParseRegionId(regionId, out int ix, out int iz))
                return false;
            minX = s_originX + ix * CellSize;
            maxX = s_originX + (ix + 1) * CellSize;
            minZ = s_originZ + iz * CellSize;
            maxZ = s_originZ + (iz + 1) * CellSize;
            return true;
        }

        /// <summary>Sample heights at center and four offsets within the cell; for debugging vertical spread.</summary>
        public static bool GetRegionSampleHeights(string regionId, out float centerY, out float minY, out float maxY)
        {
            centerY = minY = maxY = 0f;
            if (!s_initialized || string.IsNullOrEmpty(regionId) || !TryParseRegionId(regionId, out int ix, out int iz))
                return false;
            if (ZoneSystem.instance == null) return false;
            float cx = s_originX + (ix + 0.5f) * CellSize;
            float cz = s_originZ + (iz + 0.5f) * CellSize;
            float offset = CellSize * 0.4f; // stay within cell
            var sampleXZ = new[] { (cx, cz), (cx + offset, cz), (cx - offset, cz), (cx, cz + offset), (cx, cz - offset) };
            minY = float.MaxValue;
            maxY = float.MinValue;
            bool any = false;
            foreach (var (x, z) in sampleXZ)
            {
                if (GetSolidHeightAt(x, z, out float h))
                {
                    any = true;
                    if (Mathf.Abs(x - cx) < 0.01f && Mathf.Abs(z - cz) < 0.01f) centerY = h;
                    if (h < minY) minY = h;
                    if (h > maxY) maxY = h;
                }
            }
            return any;
        }

        /// <summary>
        /// Get all links from a region (doors and stairs leading to other regions).
        /// </summary>
        public static IReadOnlyList<HnaLink> GetLinksFromRegion(string regionId)
        {
            if (!s_initialized || string.IsNullOrEmpty(regionId)) return null;
            var list = new List<HnaLink>();
            foreach (var link in s_links)
            {
                if (link.FromRegionId == regionId)
                    list.Add(link);
            }
            return list;
        }

        /// <summary>
        /// Get all HNA links (door/stair connections between regions).
        /// Used by the boundary pipeline to snap elevation transitions to stair endpoints.
        /// </summary>
        public static IReadOnlyList<HnaLink> GetAllLinks()
        {
            if (!s_initialized) return new List<HnaLink>();
            return s_links;
        }

        /// <summary>
        /// Get all positions where villagers traverse links (door/stair start and end for each link).
        /// Used for in-game visualization so markers show actual traverse points.
        /// </summary>
        public static List<Vector3> GetAllLinkPositions()
        {
            var list = new List<Vector3>();
            if (!s_initialized || s_links == null) return list;
            foreach (var link in s_links)
            {
                list.Add(link.PositionStart);
                list.Add(link.PositionEnd);
            }
            return list;
        }

        /// <summary>
        /// Get world position of the center of each region (for logging and in-game visualization).
        /// Y uses BFS-discovered floor height when available, falling back to GetSolidHeightAt.
        /// </summary>
        public static List<Vector3> GetAllRegionCenters()
        {
            var list = new List<Vector3>();
            if (!s_initialized) return list;
            foreach (string id in s_regionIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!TryParseRegionId(id, out int ix, out int iz)) continue;
                float wx = s_originX + (ix + 0.5f) * CellSize;
                float wz = s_originZ + (iz + 0.5f) * CellSize;
                float wy = 0f;
                if (s_cellHeights.TryGetValue(id, out float bfsY))
                    wy = bfsY;
                else if (GetSolidHeightAt(wx, wz, out float h))
                    wy = h;
                list.Add(new Vector3(wx, wy, wz));
            }
            return list;
        }

        /// <summary>Size of height buckets for multi-floor cell IDs (m). Must match HnaPartitionHandler.</summary>
        private const float HeightBucketSize = 2f;
        private static int HeightBucket(float y) => UnityEngine.Mathf.FloorToInt(y / HeightBucketSize);

        private static bool TryParseRegionId(string id, out int ix, out int iz)
        {
            return TryParseRegionId(id, out ix, out iz, out _);
        }

        private static bool TryParseRegionId(string id, out int ix, out int iz, out int hBucket)
        {
            ix = iz = hBucket = 0;
            if (string.IsNullOrEmpty(id)) return false;
            var parts = id.Split('_');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out ix)) return false;
            if (!int.TryParse(parts[1], out iz)) return false;
            if (parts.Length >= 3 && parts[2].Length > 1 && parts[2][0] == 'h')
                int.TryParse(parts[2].Substring(1), out hBucket);
            return true;
        }

        /// <summary>
        /// Replace the graph with new partition data. Called by HnaPartitionHandler.
        /// cellHeights: BFS-discovered floor heights per cell (cell id → Y). Used for accurate marker/link Y.
        /// cellToRegion: optional map from cell id to merged region id; when set, PointToRegionId uses it.
        /// </summary>
        public static void SetGraph(float originX, float originZ, HashSet<string> regionIds, List<HnaLink> links,
            Dictionary<string, float> cellHeights = null, Dictionary<string, string> cellToRegion = null)
        {
            s_originX = originX;
            s_originZ = originZ;
            s_regionIds.Clear();
            if (regionIds != null)
            {
                foreach (var id in regionIds)
                    s_regionIds.Add(id);
            }
            s_links.Clear();
            if (links != null)
                s_links.AddRange(links);
            s_cellHeights.Clear();
            if (cellHeights != null)
            {
                foreach (var kv in cellHeights)
                    s_cellHeights[kv.Key] = kv.Value;
            }
            s_cellToRegion = cellToRegion;
            s_initialized = true;
        }

        /// <summary>
        /// Serialize the graph to a compact string for ZDO persistence.
        /// Delegates to <see cref="HnaGraphPersistence.Serialize"/>.
        /// </summary>
        public static string Serialize() => HnaGraphPersistence.Serialize();

        /// <summary>
        /// Restore graph from a serialized string (from ZDO).
        /// Delegates to <see cref="HnaGraphPersistence.Restore"/>.
        /// </summary>
        public static bool Restore(string data) => HnaGraphPersistence.Restore(data);

        /// <summary>
        /// Get all region IDs (for persistence iteration).
        /// </summary>
        internal static IEnumerable<string> GetRegionIds() => s_regionIds;

        /// <summary>
        /// Clear the graph (e.g. on world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            s_regionIds.Clear();
            s_links.Clear();
            s_cellHeights.Clear();
            s_cellToRegion = null;
            s_initialized = false;
        }

        private static string CellKey(float worldX, float worldZ, int hBucket)
        {
            int ix = Mathf.FloorToInt((worldX - s_originX) / CellSize);
            int iz = Mathf.FloorToInt((worldZ - s_originZ) / CellSize);
            return $"{ix}_{iz}_h{hBucket}";
        }

        /// <summary>
        /// Identify all boundary cells in the region graph. A cell is "boundary" if any
        /// of its 8 XZ neighbors has no region cell (the village ends there).
        /// For each XZ boundary position with multiple height buckets, only the lowest
        /// height bucket is returned (ground-level patrol).
        /// Also returns a normalized outward direction (average of missing-neighbor directions)
        /// so callers can probe from the exterior edge of the cell rather than its center.
        /// </summary>
        public static List<(string cellId, Vector3 worldCenter, Vector3 outwardDir)> GetBoundaryCells()
        {
            var result = new List<(string, Vector3, Vector3)>();
            if (!s_initialized || s_regionIds.Count == 0) return result;

            var occupiedXZ = new HashSet<long>();
            var lowestCell = new Dictionary<long, (int ix, int iz, int hBucket, string cellId)>();
            int minIx = int.MaxValue, maxIx = int.MinValue;
            int minIz = int.MaxValue, maxIz = int.MinValue;

            foreach (string id in s_regionIds)
            {
                if (!TryParseRegionId(id, out int ix, out int iz, out int hBucket)) continue;
                long key = PackXZ(ix, iz);
                occupiedXZ.Add(key);

                if (ix < minIx) minIx = ix; if (ix > maxIx) maxIx = ix;
                if (iz < minIz) minIz = iz; if (iz > maxIz) maxIz = iz;

                if (!lowestCell.TryGetValue(key, out var existing) || hBucket < existing.hBucket)
                    lowestCell[key] = (ix, iz, hBucket, id);
            }

            // Flood-fill from the grid edges to find exterior empty cells.
            // Only cells adjacent to exterior cells are true outer-boundary cells.
            var exteriorXZ = FloodFillExterior(occupiedXZ, minIx, maxIx, minIz, maxIz);

            foreach (var kvp in lowestCell)
            {
                var (ix, iz, hBucket, cellId) = kvp.Value;
                float outX = 0f, outZ = 0f;
                bool isBoundary = false;

                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    if (exteriorXZ.Contains(PackXZ(ix + dx, iz + dz)))
                    {
                        isBoundary = true;
                        outX += dx;
                        outZ += dz;
                    }
                }

                if (!isBoundary) continue;

                float wx = s_originX + (ix + 0.5f) * CellSize;
                float wz = s_originZ + (iz + 0.5f) * CellSize;

                float wy = 0f;
                if (GetSolidHeightAt(wx, wz, out float groundY))
                    wy = groundY;
                else if (s_cellHeights.TryGetValue(cellId, out float bfsY))
                    wy = bfsY;

                float mag = Mathf.Sqrt(outX * outX + outZ * outZ);
                var outDir = mag > 0.01f
                    ? new Vector3(outX / mag, 0f, outZ / mag)
                    : Vector3.forward;

                result.Add((cellId, new Vector3(wx, wy, wz), outDir));
            }

            return result;
        }

        /// <summary>
        /// BFS from the edges of the grid bounding box through empty cells.
        /// Returns the set of all empty XZ cells reachable from outside the village.
        /// Interior holes (gaps in BFS) are NOT included.
        /// </summary>
        private static HashSet<long> FloodFillExterior(
            HashSet<long> occupiedXZ, int minIx, int maxIx, int minIz, int maxIz)
        {
            // Expand bounding box by 1 cell so there's guaranteed exterior space
            int lo_x = minIx - 1, hi_x = maxIx + 1;
            int lo_z = minIz - 1, hi_z = maxIz + 1;

            var exterior = new HashSet<long>();
            var queue = new Queue<(int, int)>();

            // Seed from all edge cells of the expanded bounding box
            for (int x = lo_x; x <= hi_x; x++)
            {
                TrySeed(x, lo_z); TrySeed(x, hi_z);
            }
            for (int z = lo_z + 1; z < hi_z; z++)
            {
                TrySeed(lo_x, z); TrySeed(hi_x, z);
            }

            while (queue.Count > 0)
            {
                var (cx, cz) = queue.Dequeue();
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = cx + dx, nz = cz + dz;
                    if (nx < lo_x || nx > hi_x || nz < lo_z || nz > hi_z) continue;
                    long nk = PackXZ(nx, nz);
                    if (occupiedXZ.Contains(nk)) continue;
                    if (!exterior.Add(nk)) continue;
                    queue.Enqueue((nx, nz));
                }
            }

            return exterior;

            void TrySeed(int x, int z)
            {
                long k = PackXZ(x, z);
                if (!occupiedXZ.Contains(k) && exterior.Add(k))
                    queue.Enqueue((x, z));
            }
        }

        private static long PackXZ(int ix, int iz) => ((long)ix << 32) | (uint)iz;
    }
}
