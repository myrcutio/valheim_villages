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
            m_initialized = false;
        }

        #endregion

        #region Static registry

        private static readonly Dictionary<string, RegionGraph> s_registry = new();

        public static string VillageKey(float anchorX, float anchorZ)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:F0}_{1:F0}", anchorX, anchorZ);
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
                graph = new RegionGraph();
                s_registry[villageKey] = graph;
            }

            graph.RegisteredVillageKey = villageKey;
            return graph;
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

        public static bool GetSolidHeightNear(float worldX, float worldZ,
            float referenceY, out float height)
        {
            height = 0f;
            if (ZoneSystem.instance == null) return false;
            return ZoneSystem.instance.GetSolidHeight(
                new Vector3(worldX, referenceY + 3f, worldZ), out height, 10);
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

        public IReadOnlyDictionary<string, SurfaceKind> GetRegionKinds()
        {
            return m_regionKinds;
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

        public List<Vector3> GetAllLinkPositions()
        {
            var list = new List<Vector3>();
            if (!m_initialized) return list;
            foreach (var link in m_links)
            {
                list.Add(link.PositionStart);
                list.Add(link.PositionEnd);
            }

            return list;
        }

        public List<Vector3> GetAllRegionCenters()
        {
            var list = new List<Vector3>();
            if (!m_initialized) return list;
            foreach (var c in m_regionCentroids.Values) list.Add(c);
            return list;
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

        #endregion
    }
}