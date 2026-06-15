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
            m_outsideCellsXz.Clear();
            m_anchorReachableCellsXz.Clear();
            m_prunedPieceKeys.Clear();
            m_hasClassification = false;
            m_initialized = false;
        }

        #endregion

        #region Static registry

        // The in-memory village→graph store moved to VillageRegistry (Villages/Entity).
        // RegionGraph is now a pure instance type: each graph is owned by a Village and
        // reached only through that Village — there is no static registry, no village-key
        // bucket, and no public graph serialization seam here. This is the encapsulation
        // the "full gateway" refactor enforces.

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

        /// <summary>
        ///     Pack a 2D XZ grid cell into a long key (no height bucket). This is
        ///     the single source of truth for the 2D cell key space used by the
        ///     perimeter classification (outside / anchor-reachable terrain cells);
        ///     <see cref="RubberBandPrune" />'s internal <c>XzKey</c> delegates
        ///     here so the partition, persistence, and incremental reconcilers all
        ///     agree on the encoding. Symmetric sign handling: high half via
        ///     uint→long, low half truncates uint→int with wrap on unpack.
        /// </summary>
        internal static long PackXz(int gx, int gz)
        {
            return ((long)(uint)gx << 32) | (uint)gz;
        }

        /// <summary>Inverse of <see cref="PackXz" />.</summary>
        internal static void UnpackXz(long key, out int gx, out int gz)
        {
            gx = (int)(key >> 32);
            gz = (int)(key & 0xFFFFFFFFL);
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

        // --- Committed perimeter classification (promoted from RubberBandPrune
        // diagnostics; persisted in the v4 ZDO format). The two terrain sets are
        // 2D PackXz-keyed at LookupCellSize; the piece set is 3D PackLookup-keyed
        // (includes height bucket). These are the baseline the incremental
        // reconcilers diff against. HasClassification == false means "no committed
        // classification" → callers must fall back to a full repartition, NOT
        // treat everything as outside.
        private readonly HashSet<long> m_outsideCellsXz = new();
        private readonly HashSet<long> m_anchorReachableCellsXz = new();
        private readonly HashSet<long> m_prunedPieceKeys = new();
        private bool m_hasClassification;

        #endregion

        #region Instance properties

        public bool IsAvailable => m_initialized && m_regionIds.Count > 0;
        public int RegionCount => m_regionIds.Count;
        public int LinkCount => m_links.Count;

        /// <summary>
        ///     The key this graph is registered under in <see cref="s_registry" />.
        ///     Set by <see cref="GetOrCreate" />.
        /// </summary>
        public string RegisteredVillageKey { get; internal set; }

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
            ///     Centre of every lookup-grid cell — the exact set of points
            ///     <see cref="PointToRegionId"/> resolves to, i.e. the true operable
            ///     area of the village (concavities and holes included). This is the
            ///     source of truth for "where a villager counts as in the village",
            ///     unlike <see cref="GetAllRegionCenters"/> which returns one averaged
            ///     centroid per region. Rendered as the village-map footprint so the
            ///     map reflects real coverage rather than a derived hull.
            /// </summary>
            public List<Vector3> GetLookupCellCenters()
            {
                var list = new List<Vector3>();
                if (m_g == null || !m_g.m_initialized) return list;
                var halfCell = LookupCellSize * 0.5f;
                var halfBucket = HeightBucketSize * 0.5f;
                foreach (var key in m_g.m_lookupGrid.Keys)
                {
                    UnpackLookup(key, out var gx, out var gz, out var hb);
                    list.Add(new Vector3(
                        gx * LookupCellSize + halfCell,
                        hb * HeightBucketSize + halfBucket,
                        gz * LookupCellSize + halfCell));
                }

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

        /// <summary>
        ///     Commit the perimeter classification produced by
        ///     <see cref="RubberBandPrune.Apply" />. <paramref name="outsideXz" />
        ///     and <paramref name="anchorReachableXz" /> are 2D <see cref="PackXz" />
        ///     keys at <see cref="LookupCellSize" />; <paramref name="prunedPieceKeys" />
        ///     are 3D <see cref="PackLookup" /> keys (include height bucket). Stored
        ///     verbatim and persisted in the v4 ZDO section so loads reproduce the
        ///     committed inside/outside/piece-reachable state without re-flooding.
        /// </summary>
        public void SetClassification(
            HashSet<long> outsideXz,
            HashSet<long> anchorReachableXz,
            HashSet<long> prunedPieceKeys)
        {
            m_outsideCellsXz.Clear();
            m_anchorReachableCellsXz.Clear();
            m_prunedPieceKeys.Clear();
            if (outsideXz != null)
                foreach (var k in outsideXz) m_outsideCellsXz.Add(k);
            if (anchorReachableXz != null)
                foreach (var k in anchorReachableXz) m_anchorReachableCellsXz.Add(k);
            if (prunedPieceKeys != null)
                foreach (var k in prunedPieceKeys) m_prunedPieceKeys.Add(k);
            m_hasClassification = m_outsideCellsXz.Count > 0
                                  || m_anchorReachableCellsXz.Count > 0
                                  || m_prunedPieceKeys.Count > 0;
        }

        /// <summary>
        ///     True once a committed classification has been stored. False means
        ///     "unknown" — incremental reconcilers must fall back to a full
        ///     repartition rather than assume everything is outside.
        /// </summary>
        public bool HasClassification => m_initialized && m_hasClassification;

        /// <summary>Is the terrain cell (gx,gz) classified outside the village hull?</summary>
        public bool IsOutsideCell(int gx, int gz) =>
            m_outsideCellsXz.Contains(PackXz(gx, gz));

        /// <summary>Is the terrain cell (gx,gz) reachable from a anchor (inside)?</summary>
        public bool IsAnchorReachableCell(int gx, int gz) =>
            m_anchorReachableCellsXz.Contains(PackXz(gx, gz));

        /// <summary>Was this 3D piece lookup key pruned (not piece-reachable)?</summary>
        public bool IsPieceKeyPruned(long lookupKey) =>
            m_prunedPieceKeys.Contains(lookupKey);

        // Raw set access for the persistence layer (v4 serialization).
        internal IReadOnlyCollection<long> OutsideCellsXz => m_outsideCellsXz;
        internal IReadOnlyCollection<long> AnchorReachableCellsXz => m_anchorReachableCellsXz;
        internal IReadOnlyCollection<long> PrunedPieceKeys => m_prunedPieceKeys;

        #endregion
    }
}