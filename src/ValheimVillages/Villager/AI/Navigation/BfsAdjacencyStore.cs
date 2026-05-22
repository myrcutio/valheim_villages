using System.Collections.Generic;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Navigation
{
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

        public static void Set(Dictionary<string, HashSet<string>> adjacency,
                               HashSet<string> seeds)
        {
            Adjacency = adjacency ?? new Dictionary<string, HashSet<string>>();
            Seeds = seeds ?? new HashSet<string>();
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
        }
    }
}
