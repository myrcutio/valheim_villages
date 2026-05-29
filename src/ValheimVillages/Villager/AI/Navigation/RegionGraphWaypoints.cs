using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Reduce a cell-by-cell <see cref="RegionGraphAStar.PlanCells"/> output
    ///     to a minimal waypoint list with corner offsets. The cell sequence is
    ///     a dense list (one cell per step); waypoints are the subset of cells
    ///     where the direction changes plus the endpoints, with each turn pulled
    ///     inward by <c>agentRadius + WallClearance</c> so the agent body clears
    ///     wall-adjacent cells while pivoting.
    ///
    ///     Used by <see cref="VillagerMovement"/>'s HNA-corridor planner:
    ///     the resulting waypoints become the inputs to per-segment
    ///     <c>NavMesh.CalculatePath</c> calls, each of which must return
    ///     PathComplete and have every corner satisfy
    ///     <see cref="RegionGraph.PointToRegionId"/>.
    /// </summary>
    public static class RegionGraphWaypoints
    {
        /// <summary>
        ///     Extra clearance beyond the agent radius pulled inward at every
        ///     turn waypoint. Keeps the body's capsule from grazing a
        ///     wall-adjacent cell mid-pivot even if NavMesh.CalculatePath
        ///     would otherwise route a corner exactly at the cell boundary.
        /// </summary>
        public const float WallClearance = 0.15f;

        /// <summary>
        ///     Reduce <paramref name="cells"/> to a minimal waypoint list.
        ///     Endpoints are always included. Interior waypoints are
        ///     placed at every cell where the direction (Δcell) changes
        ///     from the previous step. Each interior waypoint is then
        ///     pulled inward by <paramref name="agentRadius"/> + <see
        ///     cref="WallClearance"/> away from any wall-adjacent neighbour
        ///     XZ.
        ///
        ///     "Wall-adjacent" means the cell has at least one 4-neighbour
        ///     XZ (at any Hb) that is NOT in the graph's lookup grid — i.e.
        ///     the boundary of the navigable region. Pulling inward away
        ///     from those neighbours moves the pivot point off the boundary
        ///     line.
        /// </summary>
        public static List<Vector3> StringPull(
            RegionGraph graph, List<CellNode> cells, float agentRadius)
        {
            if (cells == null || cells.Count == 0) return new List<Vector3>();
            if (cells.Count == 1) return new List<Vector3> { cells[0].World };

            var pull = agentRadius + WallClearance;
            var waypoints = new List<Vector3>();
            waypoints.Add(cells[0].World);

            // Walk the cell sequence and emit a waypoint at each corner
            // (direction change). Endpoints are emitted unconditionally;
            // interior waypoints get the corner offset.
            for (var i = 1; i < cells.Count - 1; i++)
            {
                var prev = cells[i - 1].Cell;
                var here = cells[i].Cell;
                var next = cells[i + 1].Cell;

                var dInX = here.Gx - prev.Gx;
                var dInZ = here.Gz - prev.Gz;
                var dInHb = here.Hb - prev.Hb;
                var dOutX = next.Gx - here.Gx;
                var dOutZ = next.Gz - here.Gz;
                var dOutHb = next.Hb - here.Hb;

                // Direction unchanged (including Hb): skip — colinear run.
                if (dInX == dOutX && dInZ == dOutZ && dInHb == dOutHb)
                    continue;

                waypoints.Add(OffsetCorner(graph, cells[i], pull));
            }

            waypoints.Add(cells[cells.Count - 1].World);
            return waypoints;
        }

        /// <summary>
        ///     Push the waypoint inward by <paramref name="pull"/> meters
        ///     away from any wall-adjacent 4-neighbour XZ. If multiple
        ///     neighbours are missing (e.g. an inside-corner cell), the
        ///     offsets sum and normalize, so the final shift points into
        ///     the open interior.
        ///
        ///     For cells with no missing neighbours (a fully interior
        ///     pivot — rare but possible at altitude transitions), no
        ///     shift is applied: the centroid is already as central as
        ///     it gets.
        /// </summary>
        private static Vector3 OffsetCorner(
            RegionGraph graph, CellNode node, float pull)
        {
            var sumDx = 0;
            var sumDz = 0;
            foreach (var (dx, dz) in s_xzSteps)
            {
                // Check the neighbour XZ at the same height bucket AND
                // its ±1 (matches PointToRegionId's resolution rule).
                if (IsWalkableAt(graph, node.Cell.Gx + dx, node.Cell.Gz + dz, node.Cell.Hb))
                    continue;
                // Missing → wall side. Push opposite (subtract the
                // neighbour direction).
                sumDx -= dx;
                sumDz -= dz;
            }

            if (sumDx == 0 && sumDz == 0) return node.World;

            var shift = new Vector3(sumDx, 0f, sumDz);
            shift.Normalize();
            shift *= pull;
            return node.World + shift;
        }

        private static bool IsWalkableAt(RegionGraph graph, int gx, int gz, int hb)
        {
            // PointToRegionId resolves the cell at (gx, gz, hb) and its
            // ±1 neighbours — same rule the planner used to admit the
            // node. Use BucketCenter to feed it the canonical sample point.
            var sample = new CellCoord(gx, gz, hb).BucketCenter();
            var id = graph.PointToRegionId(sample);
            return !string.IsNullOrEmpty(id);
        }

        private static readonly (int dx, int dz)[] s_xzSteps =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
        };
    }
}
