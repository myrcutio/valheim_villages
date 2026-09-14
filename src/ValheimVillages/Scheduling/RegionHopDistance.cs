using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Coarse distance signal (in "hops" ≈ <see cref="RegionGraph.CellSize" /> units)
    ///     between two world positions, for ETA and the triad embedding.
    ///
    ///     <para>
    ///     Distance is XZ euclidean separation divided by <see cref="RegionGraph.CellSize" />.
    ///     This deliberately does NOT BFS the partition's <c>BfsAdjacencyStore</c>: that is a
    ///     volatile, single-village, last-partition global snapshot that is stale/empty right
    ///     after a hot-reload, which made nearly every task read as unreachable.
    ///     </para>
    ///
    ///     <para>
    ///     It also no longer treats "doesn't resolve to a region" as unreachable. The region
    ///     lookup grid has holes — a villager can stand 0.16m from a cell tagged <c>p404</c>
    ///     and still get <c>PointToRegionId = null</c> — and because the scheduler is the
    ///     SOLE source of work, one unresolved endpoint used to filter every candidate and
    ///     starve the villager permanently (observed: a Farmer parked at a fire with
    ///     <c>region@pos=UNRESOLVED</c> doing nothing while 65 candidates sat on the board).
    ///     Exact reachability is enforced downstream anyway, when the assigned behavior
    ///     resolves a walkable approach and <c>BeginAssignment</c> refuses if it cannot; that
    ///     is the correct place for a hard "can't get there" verdict, because it is the one
    ///     that actually pathfinds.
    ///     </para>
    /// </summary>
    public static class RegionHopDistance
    {
        /// <summary>
        ///     Approximate hops from <paramref name="from" /> to <paramref name="to" />, or
        ///     -1 only when there is no graph to measure against. Same region → 0.
        /// </summary>
        public static int Hops(RegionGraph graph, Vector3 from, Vector3 to)
        {
            if (graph == null) return -1;
            if (SameRegion(graph, from, to)) return 0;

            var dx = from.x - to.x;
            var dz = from.z - to.z;
            var meters = Mathf.Sqrt(dx * dx + dz * dz);
            return Mathf.Max(1, Mathf.RoundToInt(meters / RegionGraph.CellSize));
        }

        /// <summary>
        ///     True when both points resolve to the SAME region — the one case worth a
        ///     special answer (0 hops, "already there"). Two unresolved points are not
        ///     treated as co-located; they fall through to euclidean.
        /// </summary>
        private static bool SameRegion(RegionGraph graph, Vector3 a, Vector3 b)
        {
            var ra = ResolveRegion(graph, a);
            return ra != null && ra == ResolveRegion(graph, b);
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
