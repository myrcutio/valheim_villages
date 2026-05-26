using System;
using System.Collections.Generic;

namespace ValheimVillages.Algorithms
{
    /// <summary>
    ///     Finds connected components (islands) from a set of elements using Union-Find.
    ///     The caller provides a connectivity predicate and a filter for which pairs to test.
    /// </summary>
    public static class ConnectedIslands
    {
        /// <summary>
        ///     Partition <paramref name="count" /> elements into connected components.
        /// </summary>
        /// <param name="count">Number of elements (indices 0..count-1).</param>
        /// <param name="isConnected">Returns true if element A and element B are connected.</param>
        /// <param name="shouldTest">
        ///     Returns true if the pair (A, B) should be tested at all.
        ///     Use this to limit testing to nearby pairs and avoid O(n^2) connectivity checks.
        /// </param>
        /// <returns>List of islands, each containing the indices of its members.</returns>
        public static List<List<int>> FindIslands(
            int count,
            Func<int, int, bool> isConnected,
            Func<int, int, bool> shouldTest)
        {
            if (count <= 0) return new List<List<int>>();

            var parent = new int[count];
            var rank = new int[count];
            for (var i = 0; i < count; i++)
                parent[i] = i;

            for (var i = 0; i < count; i++)
            for (var j = i + 1; j < count; j++)
            {
                if (Find(parent, i) == Find(parent, j))
                    continue;
                if (!shouldTest(i, j))
                    continue;
                if (isConnected(i, j))
                    Union(parent, rank, i, j);
            }

            var groups = new Dictionary<int, List<int>>();
            for (var i = 0; i < count; i++)
            {
                var root = Find(parent, i);
                if (!groups.TryGetValue(root, out var list))
                {
                    list = new List<int>();
                    groups[root] = list;
                }

                list.Add(i);
            }

            return new List<List<int>>(groups.Values);
        }

        private static int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }

            return i;
        }

        private static void Union(int[] parent, int[] rank, int a, int b)
        {
            var ra = Find(parent, a);
            var rb = Find(parent, b);
            if (ra == rb) return;

            if (rank[ra] < rank[rb])
            {
                parent[ra] = rb;
            }
            else if (rank[ra] > rank[rb])
            {
                parent[rb] = ra;
            }
            else
            {
                parent[rb] = ra;
                rank[ra]++;
            }
        }
    }
}