using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Source surface a region was extracted from. Drives per-kind tuning in
    ///     RegionBuilder (terrain has gentler edge cases than buildable pieces)
    ///     and visualization color in PathDebugRenderer.
    /// </summary>
    public enum SurfaceKind
    {
        Terrain,
        Piece,
    }

    /// <summary>
    ///     Link type between two regions (off-mesh connection).
    /// </summary>
    public enum RegionLinkType
    {
        Door,
        Stair,
        Slope,
    }

    /// <summary>
    ///     A single link between two regions (door, stair, or adjacency).
    /// </summary>
    public struct RegionLink
    {
        public string FromRegionId;
        public string ToRegionId;
        public RegionLinkType LinkType;
        public Vector3 PositionStart;
        public Vector3 PositionEnd;
    }

    /// <summary>
    ///     Spatial partition of a village's walkable area into navigable regions.
    ///     Regions are derived from NavMesh triangles; point-to-region lookup uses
    ///     a rasterized 1m grid. Each instance represents one village's graph,
    ///     stored in a static registry keyed by village anchor position. Built by
    ///     the hna_partition task.
    /// </summary>
    public class RegionGraph
    {
        /// <summary>Grid cell size in meters (XZ). Used for subdivision and region bounds.</summary>
        public const float CellSize = 3f;

        /// <summary>Size of height buckets for multi-floor lookups (m).</summary>
        internal const float HeightBucketSize = 2f;

        /// <summary>Fine-grid cell size for the rasterized point-to-region lookup (m).</summary>
        internal const float LookupCellSize = 1f;

        #region SetGraph

        /// <summary>
        ///     Set graph from triangulation build result.
        /// </summary>
        public void SetGraph(HashSet<string> regionIds, List<RegionLink> links,
            Dictionary<string, Vector3> regionCentroids,
            Dictionary<long, string> lookupGrid,
            List<(string id, Vector3 center, Vector3 outDir)> boundaryCells = null,
            Dictionary<string, SurfaceKind> regionKinds = null)
        {
            ClearInternal();
            if (regionIds != null)
                foreach (var id in regionIds)
                    m_regionIds.Add(id);
            if (links != null) m_links.AddRange(links);
            if (regionCentroids != null)
                foreach (var kv in regionCentroids)
                    m_regionCentroids[kv.Key] = kv.Value;
            if (lookupGrid != null)
                foreach (var kv in lookupGrid)
                    m_lookupGrid[kv.Key] = kv.Value;
            if (boundaryCells != null) m_boundaryCells.AddRange(boundaryCells);
            if (regionKinds != null)
                foreach (var kv in regionKinds)
                    m_regionKinds[kv.Key] = kv.Value;

            // Derive origin from centroid average for GetNearest
            if (regionCentroids != null && regionCentroids.Count > 0)
            {
                float sx = 0, sz = 0;
                foreach (var c in regionCentroids.Values)
                {
                    sx += c.x;
                    sz += c.z;
                }

                m_originX = sx / regionCentroids.Count;
                m_originZ = sz / regionCentroids.Count;
            }

            m_initialized = true;
        }

        #endregion

        #region Private helpers

        private void ClearInternal()
        {
            m_regionIds.Clear();
            m_links.Clear();
            m_regionCentroids.Clear();
            m_lookupGrid.Clear();
            m_boundaryCells.Clear();
            m_regionKinds.Clear();
            m_gates.Clear();
            m_initialized = false;
        }

        #endregion

        #region Static registry

        private static readonly Dictionary<string, RegionGraph> s_registry = new();

        /// <summary>
        ///     Bucket size (m) for <see cref="VillageKey"/> coordinate snapping.
        ///     Matches <see cref="RegionPartitionHandler"/>'s 30m
        ///     <c>RegionBuildRadius</c> — beds within one village's region-build
        ///     footprint should hash to the same key. Without this, the F0
        ///     rounding used previously gave two beds 1m apart distinct integer
        ///     keys, splitting one village's region graph across two entries
        ///     in <see cref="s_registry"/>.
        ///
        ///     <para>Limitation: any bucket is a sharp boundary — two beds 30m
        ///     apart that straddle a bucket edge still hash differently. The
        ///     correct long-term fix is to merge keys whose village perimeter
        ///     geometries intersect; that's a per-rebuild clustering step, not
        ///     a coordinate hash. Switch to that when this heuristic fails.</para>
        /// </summary>
        public const float VillageKeyBucket = 30f;

        public static string VillageKey(float anchorX, float anchorZ)
        {
            var bx = Mathf.RoundToInt(anchorX / VillageKeyBucket);
            var bz = Mathf.RoundToInt(anchorZ / VillageKeyBucket);
            return string.Format(CultureInfo.InvariantCulture, "{0}_{1}", bx, bz);
        }

        public static string VillageKey(Vector3 anchor)
        {
            return VillageKey(anchor.x, anchor.z);
        }

        public static RegionGraph GetOrCreate(string villageKey)
        {
            if (string.IsNullOrEmpty(villageKey)) villageKey = "_default";
            if (!s_registry.TryGetValue(villageKey, out var graph))
            {
                // Bucket-boundary mitigation: if a requested key falls one
                // bucket away from a key that already exists, treat them as
                // the same village and reuse the existing entry. Two beds
                // 30m apart straddling a bucket edge would otherwise hash
                // distinctly even though they belong to one village (see
                // VillageKeyBucket doc). First-come-first-served — the
                // first villager to request the partition wins the canonical
                // key. Real fix is perimeter-intersection clustering (TODO
                // tracked separately); this snap-to-neighbor keeps the
                // single-village invariant in the meantime.
                if (TryFindAdjacentRegisteredKey(villageKey, out var adjacent))
                {
                    Plugin.Log?.LogInfo(
                        $"[RegionGraph] Snapping new village key '{villageKey}' to adjacent " +
                        $"existing key '{adjacent}' (within 1 bucket; treating as same village).");
                    graph = s_registry[adjacent];
                    graph.RegisteredVillageKey = adjacent;
                    return graph;
                }

                graph = new RegionGraph();
                s_registry[villageKey] = graph;
            }

            graph.RegisteredVillageKey = villageKey;
            return graph;
        }

        /// <summary>
        ///     Returns true if any registered key is within Manhattan-1 of the
        ///     given bucket-encoded key (i.e. at most one bucket step away in
        ///     X or Z). Out parameter is the matched existing key. Only
        ///     supports the "<int>_<int>" key shape produced by
        ///     <see cref="VillageKey(float,float)"/>; falls back to "no
        ///     adjacent" for legacy "_default" or other non-bucketed keys.
        /// </summary>
        private static bool TryFindAdjacentRegisteredKey(string newKey, out string adjacent)
        {
            adjacent = null;
            if (!TryParseBucketKey(newKey, out var nx, out var nz)) return false;
            foreach (var existing in s_registry.Keys)
            {
                if (existing == newKey) continue;
                if (!TryParseBucketKey(existing, out var ex, out var ez)) continue;
                if (System.Math.Abs(ex - nx) <= 1 && System.Math.Abs(ez - nz) <= 1)
                {
                    adjacent = existing;
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseBucketKey(string key, out int bx, out int bz)
        {
            bx = bz = 0;
            if (string.IsNullOrEmpty(key)) return false;
            var sep = key.IndexOf('_');
            if (sep <= 0 || sep == key.Length - 1) return false;
            return int.TryParse(key.Substring(0, sep), NumberStyles.Integer, CultureInfo.InvariantCulture, out bx)
                   && int.TryParse(key.Substring(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out bz);
        }

        public static RegionGraph Get(string villageKey)
        {
            if (string.IsNullOrEmpty(villageKey)) return null;
            s_registry.TryGetValue(villageKey, out var graph);
            return graph;
        }

        public static RegionGraph GetNearest(Vector3 worldPos)
        {
            RegionGraph best = null;
            var bestDist = float.MaxValue;
            foreach (var graph in s_registry.Values)
            {
                if (!graph.m_initialized) continue;
                var dx = worldPos.x - graph.m_originX;
                var dz = worldPos.z - graph.m_originZ;
                var dist = dx * dx + dz * dz;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = graph;
                }
            }

            return best;
        }

        public static IEnumerable<RegionGraph> GetAll()
        {
            foreach (var graph in s_registry.Values)
                if (graph.m_initialized)
                    yield return graph;
        }

        public static bool IsAnyAvailable
        {
            get
            {
                foreach (var graph in s_registry.Values)
                    if (graph.IsAvailable)
                        return true;
                return false;
            }
        }

        [RegisterCleanup]
        public static void ClearAll()
        {
            foreach (var graph in s_registry.Values) graph.Clear();
            s_registry.Clear();
        }

        #endregion

        #region Static utilities

        public static bool GetSolidHeightAt(float worldX, float worldZ, out float height)
        {
            height = 0f;
            if (ZoneSystem.instance == null) return false;
            var fromAbove = ZoneSystem.instance.GetSolidHeight(
                new Vector3(worldX, 500f, worldZ), out var hAbove, 550);
            var fromBelow = ZoneSystem.instance.GetSolidHeight(
                new Vector3(worldX, 0f, worldZ), out var hBelow, 500);
            if (fromAbove && fromBelow)
            {
                height = Mathf.Max(hAbove, hBelow);
                return true;
            }

            if (fromAbove)
            {
                height = hAbove;
                return true;
            }

            if (fromBelow)
            {
                height = hBelow;
                return true;
            }

            return false;
        }

        public static int HeightBucket(float y)
        {
            return Mathf.FloorToInt(y / HeightBucketSize);
        }

        /// <summary>Pack lookup grid coordinates into a dictionary key.</summary>
        internal static long PackLookup(int gx, int gz, int hb)
        {
            return gx * 1_000_003L * 1_000_003L + gz * 1_000_003L + hb;
        }

        #endregion

        #region Instance fields

        private float m_originX;
        private float m_originZ;
        private readonly HashSet<string> m_regionIds = new();
        private readonly List<RegionLink> m_links = new();
        private bool m_initialized;

        private readonly Dictionary<string, Vector3> m_regionCentroids = new();

        private readonly Dictionary<long, string> m_lookupGrid = new();

        private readonly List<(string id, Vector3 center, Vector3 outDir)> m_boundaryCells = new();

        private readonly Dictionary<string, SurfaceKind> m_regionKinds = new();

        /// <summary>
        ///     World positions of detected gate/door pivots inside the village,
        ///     set by the partition (see <see cref="SetGates" />). Recomputed
        ///     each rebuild; not persisted.
        /// </summary>
        private readonly List<Vector3> m_gates = new();

        #endregion

        #region Instance properties

        public bool IsAvailable => m_initialized && m_regionIds.Count > 0;
        public int RegionCount => m_regionIds.Count;
        public int LinkCount => m_links.Count;

        /// <summary>
        ///     The key this graph is registered under in <see cref="s_registry" />.
        ///     Set by <see cref="GetOrCreate" />.
        /// </summary>
        public string RegisteredVillageKey { get; private set; }

        #endregion

        #region Instance methods

        public string PointToRegionId(Vector3 worldPosition)
        {
            if (!m_initialized) return null;

            var gx = Mathf.FloorToInt(worldPosition.x / LookupCellSize);
            var gz = Mathf.FloorToInt(worldPosition.z / LookupCellSize);
            var hb = HeightBucket(worldPosition.y);
            for (var d = 0; d <= 1; d++)
            {
                if (m_lookupGrid.TryGetValue(PackLookup(gx, gz, hb + d), out var id))
                    return id;
                if (d > 0 && m_lookupGrid.TryGetValue(PackLookup(gx, gz, hb - d), out id))
                    return id;
            }

            return null;
        }

        /// <summary>
        ///     Walk the lookup-grid cells in order of XZ distance from <paramref name="target"/>
        ///     and return the first one that satisfies <paramref name="validator"/>. Lookup-grid
        ///     cells are by construction the points that <see cref="PointToRegionId"/> will agree
        ///     with — unlike region centroids, which are geometric averages that can land in
        ///     buckets the lookup grid never indexed. This is the canonical "give me a navigable
        ///     position near here" entry point for callers that need to dispatch a villager.
        /// </summary>
        public bool TryFindNearestLookupCell(
            Vector3 target,
            System.Func<Vector3, bool> validator,
            out Vector3 worldPos,
            out string regionId,
            float maxXzDist = float.MaxValue)
        {
            worldPos = Vector3.zero;
            regionId = null;
            if (!m_initialized || m_lookupGrid.Count == 0) return false;

            var ordered = new List<(long key, Vector3 pos, string id, float distSq)>(m_lookupGrid.Count);
            var halfCell = LookupCellSize * 0.5f;
            var halfBucket = HeightBucketSize * 0.5f;
            foreach (var kv in m_lookupGrid)
            {
                UnpackLookup(kv.Key, out var gx, out var gz, out var hb);
                var wx = gx * LookupCellSize + halfCell;
                var wz = gz * LookupCellSize + halfCell;
                var wy = hb * HeightBucketSize + halfBucket;
                var pos = new Vector3(wx, wy, wz);
                var dx = wx - target.x;
                var dz = wz - target.z;
                ordered.Add((kv.Key, pos, kv.Value, dx * dx + dz * dz));
            }
            ordered.Sort((a, b) => a.distSq.CompareTo(b.distSq));

            // Bound the (potentially expensive) validator calls to cells within
            // maxXzDist of the target. Cells are distance-sorted, so once one
            // exceeds the cap every remaining one does too — break. Without
            // this, an unreachable target makes a capsule/path-validating
            // caller walk the ENTIRE grid (thousands of full corridor plans).
            var maxDistSq = maxXzDist >= float.MaxValue ? float.MaxValue : maxXzDist * maxXzDist;
            foreach (var (_, pos, id, distSq) in ordered)
            {
                if (distSq > maxDistSq) break;
                if (validator != null && !validator(pos)) continue;
                worldPos = pos;
                regionId = id;
                return true;
            }
            return false;
        }

        /// <summary>Inverse of <see cref="PackLookup"/>. Recovers grid coords + height bucket.</summary>
        internal static void UnpackLookup(long key, out int gx, out int gz, out int hb)
        {
            // Forward packing: gx * 1e6sq + gz * 1e6 + hb. Use Euclidean-style decomposition so
            // negative gx/gz/hb round-trip correctly.
            const long M = 1_000_003L;
            // hb is in [-(M-1)/2, (M-1)/2] practically; recover by mod-with-bias.
            var rem = ((key % M) + M) % M;
            hb = (int)rem;
            if (hb > M / 2) hb -= (int)M;
            var afterHb = (key - hb) / M;
            var rem2 = ((afterHb % M) + M) % M;
            gz = (int)rem2;
            if (gz > M / 2) gz -= (int)M;
            gx = (int)((afterHb - gz) / M);
        }

        public bool IsValidRegion(string regionId)
        {
            return m_initialized && !string.IsNullOrEmpty(regionId) && m_regionIds.Contains(regionId);
        }

        /// <summary>
        ///     Surface kind a region was built from. Defaults to
        ///     <see cref="SurfaceKind.Piece" /> when unset, which preserves
        ///     behavior for callers that don't care about the distinction.
        /// </summary>
        public SurfaceKind GetRegionKind(string regionId)
        {
            if (m_regionKinds.TryGetValue(regionId, out var k)) return k;
            return SurfaceKind.Piece;
        }

        public bool TryGetCellHeight(string cellId, out float height)
        {
            height = 0f;
            if (!m_initialized || cellId == null) return false;
            return m_regionCentroids.TryGetValue(cellId, out var c) && (height = c.y) == c.y;
        }

        public bool GetOrigin(out float originX, out float originZ)
        {
            originX = m_originX;
            originZ = m_originZ;
            return m_initialized;
        }

        public bool GetCellWorldXZ(string cellId, out float wx, out float wz)
        {
            wx = wz = 0f;
            if (!m_initialized || string.IsNullOrEmpty(cellId)) return false;
            if (!m_regionCentroids.TryGetValue(cellId, out var c)) return false;
            wx = c.x;
            wz = c.z;
            return true;
        }

        public bool GetRegionBounds(string regionId,
            out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            if (!m_initialized || string.IsNullOrEmpty(regionId)) return false;
            if (!m_regionCentroids.TryGetValue(regionId, out var c)) return false;
            var half = CellSize * 0.5f;
            minX = c.x - half;
            maxX = c.x + half;
            minZ = c.z - half;
            maxZ = c.z + half;
            return true;
        }

        public bool GetRegionSampleHeights(string regionId,
            out float centerY, out float minY, out float maxY)
        {
            centerY = minY = maxY = 0f;
            if (!m_initialized || string.IsNullOrEmpty(regionId)) return false;
            if (ZoneSystem.instance == null) return false;
            if (!m_regionCentroids.TryGetValue(regionId, out var c)) return false;
            float cx = c.x, cz = c.z;

            var offset = CellSize * 0.4f;
            var sampleXZ = new[]
            {
                (cx, cz), (cx + offset, cz), (cx - offset, cz),
                (cx, cz + offset), (cx, cz - offset),
            };
            minY = float.MaxValue;
            maxY = float.MinValue;
            var any = false;
            foreach (var (x, z) in sampleXZ)
            {
                if (!GetSolidHeightAt(x, z, out var h)) continue;
                any = true;
                if (Mathf.Abs(x - cx) < 0.01f && Mathf.Abs(z - cz) < 0.01f) centerY = h;
                if (h < minY) minY = h;
                if (h > maxY) maxY = h;
            }

            return any;
        }

        public IReadOnlyList<RegionLink> GetLinksFromRegion(string regionId)
        {
            if (!m_initialized || string.IsNullOrEmpty(regionId)) return null;
            var list = new List<RegionLink>();
            foreach (var link in m_links)
                if (link.FromRegionId == regionId)
                    list.Add(link);
                else if (link.ToRegionId == regionId)
                    list.Add(new RegionLink
                    {
                        FromRegionId = link.ToRegionId, ToRegionId = link.FromRegionId,
                        LinkType = link.LinkType,
                        PositionStart = link.PositionEnd, PositionEnd = link.PositionStart,
                    });
            return list;
        }

        public IReadOnlyList<RegionLink> GetAllLinks()
        {
            if (!m_initialized) return new List<RegionLink>();
            return m_links;
        }

        /// <summary>
        ///     Diagnostic accessor for region centroids. Centroids are geometric averages over
        ///     a region's triangles; they are NOT guaranteed to be inside the lookup grid
        ///     (so <see cref="PointToRegionId"/> may return null at a centroid position) and
        ///     can land in Y buckets the lookup never indexed. Safe for visualization, map
        ///     rendering, and probes — but DO NOT use for navigation. Navigation must use
        ///     <see cref="TryFindNearestLookupCell"/>, whose results are lookup-grid points
        ///     that round-trip cleanly through <see cref="PointToRegionId"/>.
        /// </summary>
        public DiagnosticsAccess Diagnostics => new DiagnosticsAccess(this);

        /// <summary>
        ///     Wrapper that gates diagnostic-only RegionGraph operations behind an explicit
        ///     namespace, so calls like <c>graph.Diagnostics.GetAllRegionCenters()</c>
        ///     visibly signal intent.
        /// </summary>
        public readonly struct DiagnosticsAccess
        {
            private readonly RegionGraph m_g;
            internal DiagnosticsAccess(RegionGraph g) { m_g = g; }

            /// <summary>
            ///     Region centroids. Visualization/diagnostic only — NOT navigation-grade.
            ///     See the doc on <see cref="RegionGraph.Diagnostics"/> for why.
            /// </summary>
            public List<Vector3> GetAllRegionCenters()
            {
                var list = new List<Vector3>();
                if (m_g == null || !m_g.m_initialized) return list;
                foreach (var c in m_g.m_regionCentroids.Values) list.Add(c);
                return list;
            }

            /// <summary>
            ///     AABB of the BFS lookup grid in XZ (the points
            ///     <see cref="PointToRegionId"/> can actually resolve). Used by
            ///     the diagnostics sidecar to compare BFS coverage vs the boundary
            ///     cells the bake produced. A mismatch is the smoking gun for the
            ///     BFS-coverage class of bugs.
            /// </summary>
            public bool TryGetLookupGridBounds(
                out float minX, out float maxX, out float minZ, out float maxZ)
            {
                minX = maxX = minZ = maxZ = 0f;
                if (m_g == null || !m_g.m_initialized || m_g.m_lookupGrid.Count == 0)
                    return false;
                minX = minZ = float.PositiveInfinity;
                maxX = maxZ = float.NegativeInfinity;
                var halfCell = LookupCellSize * 0.5f;
                foreach (var key in m_g.m_lookupGrid.Keys)
                {
                    UnpackLookup(key, out var gx, out var gz, out _);
                    var wx = gx * LookupCellSize + halfCell;
                    var wz = gz * LookupCellSize + halfCell;
                    if (wx < minX) minX = wx;
                    if (wx > maxX) maxX = wx;
                    if (wz < minZ) minZ = wz;
                    if (wz > maxZ) maxZ = wz;
                }
                return true;
            }

            /// <summary>
            ///     AABB of the boundary cells (the cells from which boundary
            ///     waypoints are seeded). When this exceeds the lookup-grid
            ///     bounds, the BFS resampler can't find a cell at waypoints the
            ///     bake placed — exactly the failure mode the sidecar surfaces.
            /// </summary>
            public bool TryGetBoundaryCellsBounds(
                out float minX, out float maxX, out float minZ, out float maxZ)
            {
                minX = maxX = minZ = maxZ = 0f;
                if (m_g == null || !m_g.m_initialized || m_g.m_boundaryCells.Count == 0)
                    return false;
                minX = minZ = float.PositiveInfinity;
                maxX = maxZ = float.NegativeInfinity;
                foreach (var (_, c, _) in m_g.m_boundaryCells)
                {
                    if (c.x < minX) minX = c.x;
                    if (c.x > maxX) maxX = c.x;
                    if (c.z < minZ) minZ = c.z;
                    if (c.z > maxZ) maxZ = c.z;
                }
                return true;
            }

            public int LookupGridCellCount => m_g != null && m_g.m_initialized ? m_g.m_lookupGrid.Count : 0;
            public int BoundaryCellCount => m_g != null && m_g.m_initialized ? m_g.m_boundaryCells.Count : 0;
        }

        internal IEnumerable<string> GetRegionIds()
        {
            return m_regionIds;
        }

        public string Serialize()
        {
            return RegionGraphPersistence.Serialize(this);
        }

        public bool Restore(string data)
        {
            return RegionGraphPersistence.Restore(this, data);
        }

        public void Clear()
        {
            ClearInternal();
        }

        public List<(string cellId, Vector3 worldCenter, Vector3 outwardDir)> GetBoundaryCells()
        {
            if (!m_initialized) return new List<(string, Vector3, Vector3)>();
            return new List<(string, Vector3, Vector3)>(m_boundaryCells);
        }

        /// <summary>
        ///     Record the gate/door pivots detected for this village. Drives the
        ///     Pass-1 outside-flood seal and the gate markers on the village map.
        /// </summary>
        public void SetGates(IEnumerable<Vector3> gates)
        {
            m_gates.Clear();
            if (gates != null) m_gates.AddRange(gates);
        }

        public List<Vector3> GetGates()
        {
            return new List<Vector3>(m_gates);
        }

        #endregion
    }
}