using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Force-directed refinement for HNA boundary waypoints.
    ///     Phase 1 pushes waypoints from cell centers toward the boundary edge and
    ///     re-samples Y at each new position. Phase 2 validates that elevation
    ///     transitions form continuous walkable chains (slope &lt;= 20°) and reverts
    ///     segments that fail chain continuity to ground level.
    /// </summary>
    internal static class WaypointRelaxation
    {
        private const float MaxSlopeAngle = 32f;
        private const int MaxIterations = 16;
        private const float SpringWeight = 0.45f;
        private const float OutwardWeight = 0.35f;
        private const float ElevationDeltaThreshold = 2f;
        private static readonly float MaxSlopeTan = Mathf.Tan(MaxSlopeAngle * Mathf.Deg2Rad);

        /// <summary>
        ///     Bake-time threshold above which a BFS/NavMesh Y disagreement is rare
        ///     enough to log individually (per-occurrence) even when bulk-deduping.
        ///     0.50m is the normal-drop threshold; values above 1.00m are outliers
        ///     worth surfacing without scrolling a summary.
        /// </summary>
        private const float ExtremeYDisagreementThreshold = 1.0f;

        /// <summary>
        ///     Per-<see cref="Refine" /> drop accumulator. Populated by
        ///     <see cref="TryResamplePosition" /> while the relaxation runs and
        ///     summarised once when the pass completes. Exposed via
        ///     <see cref="LastStats" /> for diagnostic consumers (sidecar JSON, etc.).
        /// </summary>
        internal sealed class DropStats
        {
            public int BfsMisses;
            public int NavMeshSampleFailures;
            public int YDisagreements;
            public int BakeYNaNs;
            public float WorstYDelta;
            public Vector2 WorstYDeltaXZ;
            public readonly List<DroppedWaypoint> Drops = new List<DroppedWaypoint>();

            /// <summary>
            ///     Per-pass dedup key set for individually-emitted error lines.
            ///     The same waypoint XZ is re-evaluated up to <see cref="MaxIterations" />
            ///     times within one <see cref="Refine" /> pass; without this guard
            ///     each extreme/NaN drop would log ~16 near-identical errors.
            /// </summary>
            internal readonly HashSet<string> IndividuallyEmitted = new HashSet<string>();

            public int TotalDrops => BfsMisses + NavMeshSampleFailures + YDisagreements;
        }

        /// <summary>One dropped waypoint, recorded for sidecar consumption.</summary>
        internal readonly struct DroppedWaypoint
        {
            public readonly float X;
            public readonly float Z;
            public readonly string Reason;
            public readonly float BakeY;
            public readonly float Delta;

            public DroppedWaypoint(float x, float z, string reason, float bakeY, float delta)
            {
                X = x;
                Z = z;
                Reason = reason;
                BakeY = bakeY;
                Delta = delta;
            }
        }

        /// <summary>
        ///     Snapshot of the most recently completed <see cref="Refine" /> pass.
        ///     Single-threaded; safe to read from the bake/sidecar pipeline that
        ///     runs after relaxation returns.
        /// </summary>
        internal static DropStats LastStats { get; private set; } = new DropStats();

        private static DropStats s_current;

        /// <summary>
        ///     Refine waypoints in-place using force-directed relaxation followed by
        ///     elevation chain propagation.
        /// </summary>
        internal static List<Vector3> Refine(
            List<Vector3> waypoints, Vector3 centroid, NavMeshQueryFilter filter,
            RegionGraph graph = null)
        {
            if (waypoints.Count < 4) return waypoints;

            s_current = new DropStats();
            try
            {
                RelaxXZ(waypoints, centroid, filter, graph);
                PropagateElevation(waypoints, filter);
            }
            finally
            {
                EmitSummary(s_current, waypoints.Count);
                LastStats = s_current;
                s_current = null;
            }

            return waypoints;
        }

        private static void EmitSummary(DropStats stats, int finalCount)
        {
            if (stats.TotalDrops == 0) return;

            var worst = stats.WorstYDelta > 0f
                ? $", worst |Δ|={stats.WorstYDelta:F2}m at ({stats.WorstYDeltaXZ.x:F1},{stats.WorstYDeltaXZ.y:F1})"
                : "";
            Plugin.Log?.LogWarning(
                $"[WaypointRelaxation] Pass complete: {stats.TotalDrops} waypoints dropped " +
                $"(ring={finalCount}). bfs_miss={stats.BfsMisses}, " +
                $"navmesh_fail={stats.NavMeshSampleFailures}, " +
                $"y_disagreement={stats.YDisagreements}, bake_y_nan={stats.BakeYNaNs}{worst}.");
        }

        #region Phase 1 — XZ Force-Directed Relaxation

        private static void RelaxXZ(List<Vector3> waypoints, Vector3 centroid, NavMeshQueryFilter filter,
            RegionGraph graph)
        {
            var n = waypoints.Count;
            var proposed = new Vector3[n];

            for (var iter = 0; iter < MaxIterations; iter++)
            {
                for (var i = 0; i < n; i++)
                {
                    var cur = waypoints[i];
                    var prev = waypoints[(i - 1 + n) % n];
                    var next = waypoints[(i + 1) % n];

                    var midX = (prev.x + next.x) * 0.5f;
                    var midZ = (prev.z + next.z) * 0.5f;
                    var springDx = (midX - cur.x) * SpringWeight;
                    var springDz = (midZ - cur.z) * SpringWeight;

                    var toEdgeX = cur.x - centroid.x;
                    var toEdgeZ = cur.z - centroid.z;
                    var toEdgeMag = Mathf.Sqrt(toEdgeX * toEdgeX + toEdgeZ * toEdgeZ);
                    float outDx = 0f, outDz = 0f;
                    if (toEdgeMag > 0.01f)
                    {
                        outDx = toEdgeX / toEdgeMag * OutwardWeight;
                        outDz = toEdgeZ / toEdgeMag * OutwardWeight;
                    }

                    var newX = cur.x + springDx + outDx;
                    var newZ = cur.z + springDz + outDz;

                    if (TryResamplePosition(newX, newZ, filter, graph, out var snapped))
                        proposed[i] = snapped;
                    else
                        proposed[i] = cur;
                }

                for (var i = 0; i < n; i++)
                    waypoints[i] = proposed[i];
            }
        }

        /// <summary>
        ///     Re-sample Y via BFS cell height (HNA region graph is the ground truth),
        ///     then validate against NavMesh. Fail-fast on any disagreement: if BFS
        ///     fails, NavMesh fails, or the two disagree by more than
        ///     <see cref="YDisagreementTolerance" />, log a LogError naming the
        ///     position with both Y sources and drop the waypoint. No silent
        ///     fallback to GetSolidHeightAt — a bake/graph mismatch is a real bug
        ///     that needs to surface, not be papered over.
        /// </summary>
        private const float YDisagreementTolerance = 0.5f;

        private static bool TryResamplePosition(
            float worldX, float worldZ, NavMeshQueryFilter filter,
            RegionGraph graph, out Vector3 result)
        {
            result = default;

            if (!TryGetBfsCellHeight(worldX, worldZ, graph, out var bfsY))
            {
                var bakeYDiag = RegionGraph.GetSolidHeightAt(worldX, worldZ, out var by) ? by : float.NaN;
                RecordDrop("bfs_miss", worldX, worldZ, bakeYDiag, 0f);
                return false;
            }

            var candidate = new Vector3(worldX, bfsY, worldZ);
            if (!NavMesh.SamplePosition(candidate, out var hit,
                    BoundaryGeometry.NavMeshProbeRadius, filter))
            {
                var bakeYDiag = RegionGraph.GetSolidHeightAt(worldX, worldZ, out var by) ? by : float.NaN;
                RecordDrop("navmesh_fail", worldX, worldZ, bakeYDiag, 0f);
                return false;
            }

            var deltaY = Mathf.Abs(hit.position.y - bfsY);
            if (deltaY > YDisagreementTolerance)
            {
                var bakeYDiag = RegionGraph.GetSolidHeightAt(worldX, worldZ, out var by) ? by : float.NaN;
                RecordDrop("y_disagreement", worldX, worldZ, bakeYDiag, deltaY);
                // Extreme deltas escape the dedup and emit individually — they're the
                // signal worth seeing without scrolling a summary. Per-XZ-rounded
                // dedup inside RecordDrop keeps each waypoint to one line per pass.
                if (deltaY > ExtremeYDisagreementThreshold &&
                    TryClaimIndividual("y_disagreement_extreme", worldX, worldZ))
                    Plugin.Log?.LogError(
                        "[WaypointRelaxation] BFS/NavMesh Y disagreement (extreme) at " +
                        $"({worldX:F2}, {worldZ:F2}): BFS Y={bfsY:F2}, NavMesh Y={hit.position.y:F2}, " +
                        $"bake Y={bakeYDiag:F2}, |Δ|={deltaY:F2}m. Dropping waypoint.");
                return false;
            }

            result = hit.position;
            return true;
        }

        /// <summary>
        ///     Record one dropped waypoint into the active <see cref="DropStats" />.
        ///     Bumps the per-reason counter, tracks the worst Y delta seen, appends
        ///     the drop to the per-waypoint list for sidecar consumption, and emits
        ///     individually for rare/extreme cases (NaN bake Y, anywhere) so they
        ///     don't get lost in the summary.
        /// </summary>
        private static void RecordDrop(string reason, float worldX, float worldZ, float bakeY, float delta)
        {
            var stats = s_current;
            if (stats == null) return; // Refine() not active — defensive only.

            switch (reason)
            {
                case "bfs_miss":
                    stats.BfsMisses++;
                    break;
                case "navmesh_fail":
                    stats.NavMeshSampleFailures++;
                    break;
                case "y_disagreement":
                    stats.YDisagreements++;
                    if (delta > stats.WorstYDelta)
                    {
                        stats.WorstYDelta = delta;
                        stats.WorstYDeltaXZ = new Vector2(worldX, worldZ);
                    }

                    break;
            }

            if (float.IsNaN(bakeY))
            {
                stats.BakeYNaNs++;
                // NaN bake Y is rare and a latent bug signal — always surface
                // individually, but only once per unique XZ within this pass.
                if (TryClaimIndividual("bake_y_nan", worldX, worldZ))
                    Plugin.Log?.LogError(
                        $"[WaypointRelaxation] bake Y=NaN at ({worldX:F2}, {worldZ:F2}) " +
                        $"during {reason}. Dropping waypoint; this points at a NaN-propagation " +
                        "bug upstream of relaxation.");
            }

            stats.Drops.Add(new DroppedWaypoint(worldX, worldZ, reason, bakeY, delta));
        }

        /// <summary>
        ///     Returns true the first time the given (reason, rounded XZ) is seen
        ///     within the current <see cref="Refine" /> pass. Subsequent calls with
        ///     the same key return false so callers can suppress duplicate logs.
        ///     Rounds to 0.1m to collapse iteration-to-iteration drift on the same
        ///     waypoint into a single emission.
        /// </summary>
        private static bool TryClaimIndividual(string reason, float worldX, float worldZ)
        {
            var stats = s_current;
            if (stats == null) return true;
            var key = $"{reason}:{Mathf.RoundToInt(worldX * 10f)}:{Mathf.RoundToInt(worldZ * 10f)}";
            return stats.IndividuallyEmitted.Add(key);
        }

        private static bool TryGetBfsCellHeight(float worldX, float worldZ, RegionGraph graph, out float height)
        {
            height = 0f;
            if (graph == null) return false;

            // Probe at a few heights to find the nearest region
            if (RegionGraph.GetSolidHeightAt(worldX, worldZ, out var probeY))
            {
                var regionId = graph.PointToRegionId(new Vector3(worldX, probeY, worldZ));
                if (regionId != null && graph.TryGetCellHeight(regionId, out height))
                    return true;
            }

            return false;
        }

        #endregion

        #region Phase 2 — Elevation Chain Propagation

        private static void PropagateElevation(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            var n = waypoints.Count;

            // Cache ground-level Y for every waypoint so we can revert if needed.
            var groundY = new float[n];
            for (var i = 0; i < n; i++)
                groundY[i] = waypoints[i].y;

            // Tag each waypoint as elevated or ground relative to its neighbors.
            // A steep transition (slope > 26°) means the pair can't be walked.
            var changed = true;
            var passes = 0;
            const int maxPasses = 3;

            while (changed && passes < maxPasses)
            {
                changed = false;
                passes++;

                for (var i = 0; i < n; i++)
                {
                    var next = (i + 1) % n;
                    if (!IsSteep(waypoints[i], waypoints[next])) continue;

                    var iIsLow = waypoints[i].y < waypoints[next].y;
                    var lowIdx = iIsLow ? i : next;
                    var highIdx = iIsLow ? next : i;

                    if (TryPropagateUp(waypoints, groundY, lowIdx, highIdx, filter))
                    {
                        changed = true;
                    }
                    else
                    {
                        RevertElevatedSegment(waypoints, groundY, highIdx, filter);
                        changed = true;
                    }
                }
            }
        }

        /// <summary>
        ///     Try to lift the low-side node (and its neighbors away from the high side)
        ///     up to elevated positions that maintain slopes &lt;= 26°.
        ///     Returns true if propagation produced a valid chain.
        /// </summary>
        private static bool TryPropagateUp(
            List<Vector3> waypoints, float[] groundY,
            int lowIdx, int highIdx, NavMeshQueryFilter filter)
        {
            var n = waypoints.Count;
            var direction = StepDirection(lowIdx, highIdx, n);

            // Walk from lowIdx away from highIdx, collecting candidates.
            var candidates = new List<(int idx, Vector3 pos)>();
            var cur = lowIdx;

            for (var step = 0; step < n; step++)
            {
                var toward = (cur - direction + n) % n; // neighbor toward high side
                var targetY = candidates.Count > 0
                    ? candidates[candidates.Count - 1].pos.y
                    : waypoints[highIdx].y;

                if (!TryFindElevatedPosition(waypoints[cur], targetY, filter, out var elevated))
                    return false;

                if (!IsSteep(elevated, waypoints[toward]) || toward == highIdx)
                {
                    candidates.Add((cur, elevated));

                    // Validate the full chain we've built.
                    if (ValidateChain(waypoints, candidates, highIdx))
                    {
                        foreach (var (idx, pos) in candidates)
                        {
                            waypoints[idx] = pos;
                            groundY[idx] = pos.y;
                        }

                        return true;
                    }
                }
                else
                {
                    candidates.Add((cur, elevated));
                }

                var nextCur = (cur + direction + n) % n;
                if (nextCur == highIdx) return false; // wrapped around
                cur = nextCur;
            }

            return false;
        }

        /// <summary>
        ///     Try to find an elevated NavMesh position near the waypoint's XZ that keeps
        ///     the slope to the target Y within limits.
        /// </summary>
        private static bool TryFindElevatedPosition(
            Vector3 current, float targetY, NavMeshQueryFilter filter, out Vector3 result)
        {
            result = current;

            var probe = new Vector3(current.x, targetY, current.z);
            if (!NavMesh.SamplePosition(probe, out var hit,
                    BoundaryGeometry.NavMeshProbeRadius, filter))
                return false;

            // Reject if the sampled position is still at ground level
            // (didn't actually find an elevated surface).
            if (Mathf.Abs(hit.position.y - current.y) < 0.5f)
                return false;

            result = hit.position;
            return true;
        }

        /// <summary>
        ///     Validate that all pairs in the candidate chain + existing waypoints
        ///     have slopes &lt;= 26°.
        /// </summary>
        private static bool ValidateChain(
            List<Vector3> waypoints,
            List<(int idx, Vector3 pos)> candidates,
            int highIdx)
        {
            if (candidates.Count == 0) return true;

            // Build a lookup of candidate positions.
            var posLookup = new Dictionary<int, Vector3>();
            foreach (var (idx, pos) in candidates)
                posLookup[idx] = pos;

            Vector3 Pos(int i)
            {
                return posLookup.TryGetValue(i, out var p) ? p : waypoints[i];
            }

            var n = waypoints.Count;

            // Check slope between consecutive candidates and between
            // the end candidates and their non-candidate neighbors.
            for (var c = 0; c < candidates.Count; c++)
            {
                var idx = candidates[c].idx;
                var prev = (idx - 1 + n) % n;
                var next = (idx + 1) % n;

                if (IsSteep(Pos(idx), Pos(prev))) return false;
                if (IsSteep(Pos(idx), Pos(next))) return false;
            }

            // Also check the transition to highIdx.
            if (IsSteep(Pos(candidates[0].idx), Pos(highIdx))) return false;

            return true;
        }

        /// <summary>
        ///     Revert an elevated segment to ground-level positions. Starting from
        ///     highIdx, walk through contiguous elevated nodes and re-sample them
        ///     at ground level via NavMesh.
        /// </summary>
        private static void RevertElevatedSegment(
            List<Vector3> waypoints, float[] groundY,
            int highIdx, NavMeshQueryFilter filter)
        {
            var n = waypoints.Count;
            var baseY = MinNeighborY(waypoints, highIdx);

            var toRevert = new List<int> { highIdx };

            // Expand in both directions to find the full elevated segment.
            for (var dir = -1; dir <= 1; dir += 2)
            {
                var cur = (highIdx + dir + n) % n;
                var steps = 0;
                while (cur != highIdx && steps < n)
                {
                    var dy = Mathf.Abs(waypoints[cur].y - baseY);
                    if (dy < ElevationDeltaThreshold) break;
                    toRevert.Add(cur);
                    cur = (cur + dir + n) % n;
                    steps++;
                }
            }

            foreach (var idx in toRevert)
            {
                var wp = waypoints[idx];
                var probe = new Vector3(wp.x, baseY, wp.z);

                if (NavMesh.SamplePosition(probe, out var hit,
                        BoundaryGeometry.NavMeshProbeRadius, filter))
                {
                    waypoints[idx] = hit.position;
                    groundY[idx] = hit.position.y;
                }
            }
        }

        #endregion

        #region Helpers

        private static bool IsSteep(Vector3 a, Vector3 b)
        {
            var dy = Mathf.Abs(a.y - b.y);
            var dxz = Vector2.Distance(
                new Vector2(a.x, a.z), new Vector2(b.x, b.z));

            if (dxz < 0.01f) return dy > 0.3f;
            return dy / dxz > MaxSlopeTan;
        }

        /// <summary>
        ///     Determine which direction to step through the ring from lowIdx
        ///     away from highIdx. Returns +1 or -1.
        /// </summary>
        private static int StepDirection(int lowIdx, int highIdx, int n)
        {
            var fwd = (highIdx - lowIdx + n) % n;
            // Step in the direction away from highIdx.
            return fwd <= n / 2 ? -1 : 1;
        }

        private static float MinNeighborY(List<Vector3> waypoints, int idx)
        {
            var n = waypoints.Count;
            var yPrev = waypoints[(idx - 1 + n) % n].y;
            var yNext = waypoints[(idx + 1) % n].y;
            return Mathf.Min(yPrev, yNext);
        }

        #endregion
    }
}