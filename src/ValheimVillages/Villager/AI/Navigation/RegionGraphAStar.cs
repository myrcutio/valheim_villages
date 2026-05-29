using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Cell coordinate in a <see cref="RegionGraph"/>'s LookupGrid space.
    ///     (gx, gz) are XZ grid indices at <see cref="RegionGraph.LookupCellSize"/>.
    ///     hb is the height bucket index at <see cref="RegionGraph.HeightBucketSize"/>.
    ///     A CellCoord identifies a single 1×1×2m volume that <see cref="RegionGraph.PointToRegionId"/>
    ///     would resolve to a unique region.
    /// </summary>
    public readonly struct CellCoord
    {
        public readonly int Gx;
        public readonly int Gz;
        public readonly int Hb;

        public CellCoord(int gx, int gz, int hb)
        {
            Gx = gx; Gz = gz; Hb = hb;
        }

        /// <summary>
        ///     World-space center of this cell's XZ footprint at the height
        ///     bucket's midpoint Y. Use this only when you do NOT have the
        ///     cell's region centroid available — the region centroid is
        ///     more accurate (it tracks the actual triangle distribution),
        ///     this is a geometric center of the grid cell.
        /// </summary>
        public Vector3 BucketCenter()
        {
            return new Vector3(
                Gx * RegionGraph.LookupCellSize + RegionGraph.LookupCellSize * 0.5f,
                Hb * RegionGraph.HeightBucketSize + RegionGraph.HeightBucketSize * 0.5f,
                Gz * RegionGraph.LookupCellSize + RegionGraph.LookupCellSize * 0.5f);
        }

        public long Pack()
        {
            return RegionGraph.PackLookup(Gx, Gz, Hb);
        }

        public static CellCoord FromWorld(Vector3 worldPosition)
        {
            return new CellCoord(
                Mathf.FloorToInt(worldPosition.x / RegionGraph.LookupCellSize),
                Mathf.FloorToInt(worldPosition.z / RegionGraph.LookupCellSize),
                RegionGraph.HeightBucket(worldPosition.y));
        }

        public override string ToString()
        {
            return $"({Gx},{Gz},{Hb})";
        }
    }

    /// <summary>
    ///     Per-cell node in an <see cref="RegionGraphAStar.PlanCells"/> result.
    ///     Exposes both the cell coord and the world position the planner used
    ///     for cost evaluation (region centroid when available, falling back to
    ///     the cell's bucket center). Consumers should use <see cref="World"/>
    ///     for waypoint generation, not <see cref="BucketCenter"/>, because the
    ///     centroid better matches where NavMesh.CalculatePath will route.
    /// </summary>
    public readonly struct CellNode
    {
        public readonly CellCoord Cell;
        public readonly string RegionId;
        public readonly Vector3 World;

        public CellNode(CellCoord cell, string regionId, Vector3 world)
        {
            Cell = cell; RegionId = regionId; World = world;
        }
    }

    /// <summary>
    ///     Reason a <see cref="RegionGraphAStar.PlanCells"/> call failed to
    ///     produce a cell sequence. Surfaced in the [PathPlan] event so the
    ///     log distinguishes the four distinct failure modes (start
    ///     off-graph, goal off-graph, A* found no connection, graph
    ///     unavailable) without requiring a probe.
    /// </summary>
    public enum AStarFailReason
    {
        /// <summary>Path found — not a failure.</summary>
        None,
        /// <summary>Graph parameter was null or not initialized.</summary>
        GraphUnavailable,
        /// <summary>fromWorld did not resolve to any cell in the lookup grid.</summary>
        StartOffGraph,
        /// <summary>toWorld did not resolve to any cell in the lookup grid.</summary>
        GoalOffGraph,
        /// <summary>Both endpoints resolved but no connecting cell sequence exists.</summary>
        NoConnection,
    }

    /// <summary>
    ///     Cell-level A* over a <see cref="RegionGraph"/>'s LookupGrid. Nodes are
    ///     (gx, gz, hb) cells drawn from <c>m_lookupGrid</c> — by construction the
    ///     points <see cref="RegionGraph.PointToRegionId"/> agrees with. 4-connected
    ///     XZ neighbours, cross-bucket edges gated by <see cref="MaxClimb"/> on
    ///     region-centroid Y (same primitive RubberBandPrune Pass 3 uses to
    ///     decide piece-step reachability).
    ///
    ///     This is the foundation of Prong A in the path-must-stay-on-hna workflow:
    ///     by planning over the HNA lookup grid first and only running
    ///     NavMesh.CalculatePath per inter-waypoint segment, we guarantee every
    ///     path corner sits on a cell <see cref="RegionGraph.PointToRegionId"/>
    ///     can resolve — i.e., the path stays on the HNA graph by construction,
    ///     not as a post-hoc validation.
    /// </summary>
    public static class RegionGraphAStar
    {
        /// <summary>
        ///     Maximum Y delta in meters allowed between two cells' region
        ///     centroids for an A* edge to exist between them. Matches the
        ///     constant RubberBandPrune Pass 3 uses to gate the piece flood
        ///     (see RubberBandPrune.cs:395). Set the two intentionally
        ///     together: relaxing one and not the other would let A* find
        ///     paths the prune does not believe are physically reachable.
        /// </summary>
        public const float MaxClimb = 1.0f;

        /// <summary>
        ///     Per-step XZ candidates (4-connected; diagonals skipped because
        ///     they would let A* cut across wall corners between two cells
        ///     whose only connection is the diagonal).
        /// </summary>
        private static readonly (int dx, int dz)[] s_xzSteps =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
        };

        /// <summary>
        ///     Per-step Y bucket deltas to try. Limited to ±1 because
        ///     HeightBucketSize=2m and MaxClimb=1m — a step that landed two
        ///     buckets away would be at least 2m of vertical change, which
        ///     the MaxClimb gate would reject anyway. Same-bucket (dy=0)
        ///     listed first so the search prefers level steps.
        /// </summary>
        private static readonly int[] s_hbSteps = { 0, 1, -1 };

        /// <summary>
        ///     Plan a cell-level path from <paramref name="fromWorld"/> to
        ///     <paramref name="toWorld"/> over <paramref name="graph"/>'s
        ///     LookupGrid. Returns the cell sequence (start cell first,
        ///     goal cell last) or null if either endpoint cannot be resolved
        ///     to a lookup cell, or no path exists between them.
        /// </summary>
        public static List<CellNode> PlanCells(
            RegionGraph graph, Vector3 fromWorld, Vector3 toWorld)
        {
            return PlanCells(graph, fromWorld, toWorld, out _);
        }

        public static List<CellNode> PlanCells(
            RegionGraph graph, Vector3 fromWorld, Vector3 toWorld,
            out AStarFailReason failReason)
        {
            failReason = AStarFailReason.None;
            if (graph == null || !graph.IsAvailable)
            {
                failReason = AStarFailReason.GraphUnavailable;
                return null;
            }

            // Resolve start/goal to lookup cells. Both endpoints must map to
            // a cell PointToRegionId can return — fail closed (return null)
            // if either fails, so the caller surfaces PathUnreachable rather
            // than silently extending the search outside the lookup grid.
            if (!TryResolveCell(graph, fromWorld, out var startCell, out var startRegion))
            {
                failReason = AStarFailReason.StartOffGraph;
                return null;
            }
            if (!TryResolveCell(graph, toWorld, out var goalCell, out var goalRegion))
            {
                failReason = AStarFailReason.GoalOffGraph;
                return null;
            }

            // Identical cell: trivial single-node path.
            if (startCell.Pack() == goalCell.Pack())
            {
                graph.TryGetCellHeight(startRegion, out _);
                var startCentroid = ResolveWorld(graph, startCell, startRegion);
                return new List<CellNode> { new CellNode(startCell, startRegion, startCentroid) };
            }

            var goalCentroid = ResolveWorld(graph, goalCell, goalRegion);

            // Standard A* on a sparse grid. Open set as a sorted dictionary
            // is overkill for typical village sizes (~hundreds of cells); a
            // plain list with linear-scan extract-min is fine and avoids a
            // BinaryHeap dependency. If performance ever shows up in
            // profiling, swap for a min-heap.
            var came = new Dictionary<long, long>(); // cell.Pack -> prev cell.Pack
            var gScore = new Dictionary<long, float>();
            var fScore = new Dictionary<long, float>();
            var open = new List<long>();
            var openSet = new HashSet<long>();
            var closed = new HashSet<long>();

            var startKey = startCell.Pack();
            gScore[startKey] = 0f;
            fScore[startKey] = Vector3.Distance(
                ResolveWorld(graph, startCell, startRegion), goalCentroid);
            open.Add(startKey);
            openSet.Add(startKey);

            while (open.Count > 0)
            {
                // Extract min f-score.
                var bestIdx = 0;
                var bestF = fScore.TryGetValue(open[0], out var f0) ? f0 : float.MaxValue;
                for (var i = 1; i < open.Count; i++)
                {
                    var fi = fScore.TryGetValue(open[i], out var f) ? f : float.MaxValue;
                    if (fi < bestF) { bestF = fi; bestIdx = i; }
                }

                var currentKey = open[bestIdx];
                open.RemoveAt(bestIdx);
                openSet.Remove(currentKey);

                if (currentKey == goalCell.Pack())
                    return Reconstruct(graph, came, currentKey, startKey);

                closed.Add(currentKey);

                RegionGraph.UnpackLookup(currentKey, out var cgx, out var cgz, out var chb);
                var currentRegion = graph.PointToRegionId(
                    new CellCoord(cgx, cgz, chb).BucketCenter());
                if (string.IsNullOrEmpty(currentRegion)) continue;
                graph.TryGetCellHeight(currentRegion, out var currentY);

                foreach (var (dx, dz) in s_xzSteps)
                foreach (var dhb in s_hbSteps)
                {
                    var ncell = new CellCoord(cgx + dx, cgz + dz, chb + dhb);
                    var nkey = ncell.Pack();
                    if (closed.Contains(nkey)) continue;

                    // Must be a cell PointToRegionId would resolve.
                    // PointToRegionId itself searches ±1 hb, so we re-use
                    // it (instead of poking m_lookupGrid directly) to keep
                    // the planner's notion of "is this cell walkable"
                    // identical to the runtime point query.
                    var nRegion = graph.PointToRegionId(ncell.BucketCenter());
                    if (string.IsNullOrEmpty(nRegion)) continue;
                    if (!graph.TryGetCellHeight(nRegion, out var nY)) continue;

                    // MaxClimb gate on region-centroid Y. Matches
                    // RubberBandPrune Pass 3's piece-step gate exactly;
                    // any relaxation here would let the planner find
                    // paths the prune does not believe are reachable.
                    if (Mathf.Abs(nY - currentY) > MaxClimb) continue;

                    // Cost = Euclidean between the two cells' centroids.
                    // Using centroid (not bucket center) so cost reflects
                    // actual triangle geometry the bake produced.
                    var nCentroid = ResolveWorld(graph, ncell, nRegion);
                    var currentCentroid = ResolveWorld(graph,
                        new CellCoord(cgx, cgz, chb), currentRegion);
                    var stepCost = Vector3.Distance(currentCentroid, nCentroid);
                    var tentativeG =
                        (gScore.TryGetValue(currentKey, out var cg) ? cg : float.MaxValue) + stepCost;

                    if (gScore.TryGetValue(nkey, out var existingG) && tentativeG >= existingG)
                        continue;

                    came[nkey] = currentKey;
                    gScore[nkey] = tentativeG;
                    fScore[nkey] = tentativeG + Vector3.Distance(nCentroid, goalCentroid);
                    if (openSet.Add(nkey))
                        open.Add(nkey);
                }
            }

            failReason = AStarFailReason.NoConnection;
            return null;
        }

        /// <summary>
        ///     Resolve a world-space position to the LookupGrid cell that
        ///     <see cref="RegionGraph.PointToRegionId"/> would map it to,
        ///     plus the region ID. Returns false if the position resolves
        ///     to no cell — the caller must treat that as PathUnreachable
        ///     rather than fall through to a wider search.
        /// </summary>
        private static bool TryResolveCell(
            RegionGraph graph, Vector3 world,
            out CellCoord cell, out string regionId)
        {
            cell = CellCoord.FromWorld(world);
            regionId = graph.PointToRegionId(world);
            if (!string.IsNullOrEmpty(regionId)) return true;

            // PointToRegionId already searches ±1 height bucket; if it
            // missed, the position is genuinely off-grid. Fail closed.
            regionId = null;
            return false;
        }

        /// <summary>
        ///     World position used for cost evaluation. Prefers the region
        ///     centroid (tracks actual triangle distribution) and falls
        ///     back to the bucket center if the centroid is missing.
        /// </summary>
        private static Vector3 ResolveWorld(
            RegionGraph graph, CellCoord cell, string regionId)
        {
            if (!string.IsNullOrEmpty(regionId) &&
                graph.GetCellWorldXZ(regionId, out var wx, out var wz) &&
                graph.TryGetCellHeight(regionId, out var wy))
                return new Vector3(wx, wy, wz);
            return cell.BucketCenter();
        }

        private static List<CellNode> Reconstruct(
            RegionGraph graph,
            Dictionary<long, long> came, long endKey, long startKey)
        {
            var reverse = new List<long>();
            var cur = endKey;
            reverse.Add(cur);
            while (cur != startKey)
            {
                if (!came.TryGetValue(cur, out var prev)) return null;
                cur = prev;
                reverse.Add(cur);
            }

            reverse.Reverse();
            var result = new List<CellNode>(reverse.Count);
            foreach (var key in reverse)
            {
                RegionGraph.UnpackLookup(key, out var gx, out var gz, out var hb);
                var cell = new CellCoord(gx, gz, hb);
                var region = graph.PointToRegionId(cell.BucketCenter());
                result.Add(new CellNode(cell, region, ResolveWorld(graph, cell, region)));
            }

            return result;
        }
    }
}
