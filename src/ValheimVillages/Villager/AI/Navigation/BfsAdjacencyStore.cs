using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Source(s) of an edge in <see cref="BfsAdjacencyStore.Adjacency"/>.
    /// Edges can have multiple sources (e.g. an in-pass shared edge that
    /// also happens to coincide on a quantized vertex); kinds OR together.
    /// </summary>
    [System.Flags]
    public enum BfsEdgeKind
    {
        None = 0,
        /// <summary>Two regions in the same pass share a triangle edge (2 vertices). Geometrically honest.</summary>
        InPassEdge = 1,
        /// <summary>Cross-kind: terrain and piece regions share a single quantized vertex position (25cm bucket).</summary>
        CrossVert = 2,
        /// <summary>Cross-kind: any vertex pair within 0.5m vertex-to-vertex.</summary>
        CrossProx = 4,
    }

    /// <summary>
    /// Per-edge metadata captured at <c>RecordCrossKindAdjacency</c> time
    /// for the <c>vv_bfs_trace</c> diagnostic. Tells us *why* two regions
    /// are connected in the BFS graph (which edge kind), and for cross-kind
    /// edges where the bridge geometrically sits.
    /// </summary>
    public struct BfsEdgeMeta
    {
        public BfsEdgeKind Kinds;
        /// <summary>World position of a representative shared/near vertex; set by CrossVert / CrossProx edges only.</summary>
        public Vector3? RepresentativePos;
        /// <summary>Min vertex-to-vertex distance; meaningful only when Kinds includes CrossProx.</summary>
        public float ProxMinDist;
    }

    /// <summary>
    /// Snapshot of the cross-kind BFS adjacency graph and seed set from the
    /// most recent partition. Persisted so diagnostic commands like
    /// vv_bfs_trace can compute and highlight paths back to bed seeds
    /// without re-running the partition.
    ///
    /// Populated by <c>RegionPartitionHandler.RecordCrossKindAdjacency</c>.
    /// Cleared on hot reload via <see cref="RegisterCleanupAttribute"/>.
    /// </summary>
    public static class BfsAdjacencyStore
    {
        public static Dictionary<string, HashSet<string>> Adjacency
            { get; private set; } = new Dictionary<string, HashSet<string>>();

        public static HashSet<string> Seeds
            { get; private set; } = new HashSet<string>();

        /// <summary>
        /// Per-edge metadata keyed by <see cref="EdgeKey"/>. An edge between
        /// A and B has exactly one entry under the canonical sorted-pair key.
        /// </summary>
        public static Dictionary<string, BfsEdgeMeta> EdgeMeta
            { get; private set; } = new Dictionary<string, BfsEdgeMeta>();

        /// <summary>
        /// Canonical key for the undirected edge {a, b}. Sorts the two
        /// region IDs ordinally so EdgeKey("t1","t2") == EdgeKey("t2","t1").
        /// </summary>
        public static string EdgeKey(string a, string b)
        {
            if (string.CompareOrdinal(a, b) <= 0) return a + "|" + b;
            return b + "|" + a;
        }

        public static void Set(Dictionary<string, HashSet<string>> adjacency,
                               HashSet<string> seeds,
                               Dictionary<string, BfsEdgeMeta> edgeMeta)
        {
            Adjacency = adjacency ?? new Dictionary<string, HashSet<string>>();
            Seeds = seeds ?? new HashSet<string>();
            EdgeMeta = edgeMeta ?? new Dictionary<string, BfsEdgeMeta>();
        }

        /// <summary>
        /// Shortest path from <paramref name="targetRegionId"/> back to any
        /// seed region via reverse BFS through <see cref="Adjacency"/>.
        /// Returns null if no path exists. Path ordered target -> seed.
        /// </summary>
        public static List<string> PathToSeed(string targetRegionId)
        {
            if (string.IsNullOrEmpty(targetRegionId)) return null;
            if (Adjacency == null || Adjacency.Count == 0) return null;
            if (Seeds == null || Seeds.Count == 0) return null;

            var parent = new Dictionary<string, string> { { targetRegionId, null } };
            var queue = new Queue<string>();
            queue.Enqueue(targetRegionId);

            while (queue.Count > 0)
            {
                string cur = queue.Dequeue();
                if (Seeds.Contains(cur))
                {
                    var path = new List<string>();
                    string n = cur;
                    while (n != null)
                    {
                        path.Add(n);
                        parent.TryGetValue(n, out n);
                    }
                    path.Reverse();
                    return path;
                }
                if (!Adjacency.TryGetValue(cur, out var neighbors)) continue;
                foreach (var n in neighbors)
                {
                    if (parent.ContainsKey(n)) continue;
                    parent[n] = cur;
                    queue.Enqueue(n);
                }
            }
            return null;
        }

        [RegisterCleanup]
        public static void Clear()
        {
            Adjacency = new Dictionary<string, HashSet<string>>();
            Seeds = new HashSet<string>();
            EdgeMeta = new Dictionary<string, BfsEdgeMeta>();
        }
    }
}
