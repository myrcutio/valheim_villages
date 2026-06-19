using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Coarse distance signal (in "hops" ≈ <see cref="RegionGraph.CellSize" /> units)
    ///     between two world positions, for ETA and the triad embedding.
    ///
    ///     <para>
    ///     Reachability semantics match how the work behaviors actually decide it: a point
    ///     is reachable if it <i>resolves to a region</i> in the village graph (the agent
    ///     crosses NavMeshLinks at runtime, so a region-resident point is reachable even
    ///     across links). Distance is then the XZ euclidean separation divided by
    ///     <see cref="RegionGraph.CellSize" />.
    ///     </para>
    ///
    ///     <para>
    ///     This deliberately does NOT BFS the partition's <c>BfsAdjacencyStore</c>: that is a
    ///     volatile, single-village, last-partition global snapshot that is stale/empty right
    ///     after a hot-reload, which made nearly every task read as unreachable. Euclidean is
    ///     wall-blind, but exact reachability is enforced downstream when a behavior resolves
    ///     a walkable approach at assignment time.
    ///     </para>
    /// </summary>
    public static class RegionHopDistance
    {
        /// <summary>
        ///     Approximate hops from <paramref name="from" /> to <paramref name="to" />, or
        ///     -1 if either endpoint doesn't resolve to a region (off-graph → unreachable).
        ///     Same region → 0.
        /// </summary>
        public static int Hops(RegionGraph graph, Vector3 from, Vector3 to)
        {
            if (graph == null) return -1;
            var a = ResolveRegion(graph, from);
            var b = ResolveRegion(graph, to);
            if (a == null || b == null) return -1; // off the village graph
            if (a == b) return 0;

            var dx = from.x - to.x;
            var dz = from.z - to.z;
            var meters = Mathf.Sqrt(dx * dx + dz * dz);
            return Mathf.Max(1, Mathf.RoundToInt(meters / RegionGraph.CellSize));
        }

        private static string ResolveRegion(RegionGraph graph, Vector3 pos)
        {
            var id = graph.PointToRegionId(pos);
            if (id != null) return id;
            // Off-mesh (perimeter piece, off-by-a-cell): snap to the nearest indexed cell.
            // Generous radius so wall/perimeter pieces still resolve as part of the village.
            return graph.TryFindNearestLookupCell(pos, null, out _, out var nearId, 6f) ? nearId : null;
        }
    }
}
