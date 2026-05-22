using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Force-directed refinement for HNA boundary waypoints.
    /// Phase 1 pushes waypoints from cell centers toward the boundary edge and
    /// re-samples Y at each new position. Phase 2 validates that elevation
    /// transitions form continuous walkable chains (slope &lt;= 20°) and reverts
    /// segments that fail chain continuity to ground level.
    /// </summary>
    internal static class WaypointRelaxation
    {
        const float MaxSlopeAngle = 32f;
        static readonly float MaxSlopeTan = Mathf.Tan(MaxSlopeAngle * Mathf.Deg2Rad);
        const int MaxIterations = 16;
        const float SpringWeight = 0.45f;
        const float OutwardWeight = 0.35f;
        const float ElevationDeltaThreshold = 2f;

        /// <summary>
        /// Refine waypoints in-place using force-directed relaxation followed by
        /// elevation chain propagation.
        /// </summary>
        internal static List<Vector3> Refine(
            List<Vector3> waypoints, Vector3 centroid, NavMeshQueryFilter filter,
            RegionGraph graph = null)
        {
            if (waypoints.Count < 4) return waypoints;

            RelaxXZ(waypoints, centroid, filter, graph);
            PropagateElevation(waypoints, filter);

            return waypoints;
        }

        #region Phase 1 — XZ Force-Directed Relaxation

        static void RelaxXZ(List<Vector3> waypoints, Vector3 centroid, NavMeshQueryFilter filter,
            RegionGraph graph)
        {
            int n = waypoints.Count;
            var proposed = new Vector3[n];

            for (int iter = 0; iter < MaxIterations; iter++)
            {
                for (int i = 0; i < n; i++)
                {
                    var cur = waypoints[i];
                    var prev = waypoints[(i - 1 + n) % n];
                    var next = waypoints[(i + 1) % n];

                    float midX = (prev.x + next.x) * 0.5f;
                    float midZ = (prev.z + next.z) * 0.5f;
                    float springDx = (midX - cur.x) * SpringWeight;
                    float springDz = (midZ - cur.z) * SpringWeight;

                    float toEdgeX = cur.x - centroid.x;
                    float toEdgeZ = cur.z - centroid.z;
                    float toEdgeMag = Mathf.Sqrt(toEdgeX * toEdgeX + toEdgeZ * toEdgeZ);
                    float outDx = 0f, outDz = 0f;
                    if (toEdgeMag > 0.01f)
                    {
                        outDx = (toEdgeX / toEdgeMag) * OutwardWeight;
                        outDz = (toEdgeZ / toEdgeMag) * OutwardWeight;
                    }

                    float newX = cur.x + springDx + outDx;
                    float newZ = cur.z + springDz + outDz;

                    if (TryResamplePosition(newX, newZ, filter, graph, out Vector3 snapped))
                        proposed[i] = snapped;
                    else
                        proposed[i] = cur;
                }

                for (int i = 0; i < n; i++)
                    waypoints[i] = proposed[i];
            }
        }

        /// <summary>
        /// Re-sample Y via BFS cell height (accurate per-floor) with
        /// GetSolidHeightAt fallback, then validate on NavMesh.
        /// </summary>
        static bool TryResamplePosition(
            float worldX, float worldZ, NavMeshQueryFilter filter,
            RegionGraph graph, out Vector3 result)
        {
            result = default;

            float probeY;
            if (!TryGetBfsCellHeight(worldX, worldZ, graph, out probeY))
            {
                if (!RegionGraph.GetSolidHeightAt(worldX, worldZ, out probeY))
                    return false;
            }

            var candidate = new Vector3(worldX, probeY, worldZ);
            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit,
                    BoundaryGeometry.NavMeshProbeRadius, filter))
                return false;

            result = hit.position;
            return true;
        }

        static bool TryGetBfsCellHeight(float worldX, float worldZ, RegionGraph graph, out float height)
        {
            height = 0f;
            if (graph == null) return false;

            // Probe at a few heights to find the nearest region
            if (RegionGraph.GetSolidHeightAt(worldX, worldZ, out float probeY))
            {
                string regionId = graph.PointToRegionId(new Vector3(worldX, probeY, worldZ));
                if (regionId != null && graph.TryGetCellHeight(regionId, out height))
                    return true;
            }

            return false;
        }

        #endregion

        #region Phase 2 — Elevation Chain Propagation

        static void PropagateElevation(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            int n = waypoints.Count;

            // Cache ground-level Y for every waypoint so we can revert if needed.
            var groundY = new float[n];
            for (int i = 0; i < n; i++)
                groundY[i] = waypoints[i].y;

            // Tag each waypoint as elevated or ground relative to its neighbors.
            // A steep transition (slope > 26°) means the pair can't be walked.
            bool changed = true;
            int passes = 0;
            const int maxPasses = 3;

            while (changed && passes < maxPasses)
            {
                changed = false;
                passes++;

                for (int i = 0; i < n; i++)
                {
                    int next = (i + 1) % n;
                    if (!IsSteep(waypoints[i], waypoints[next])) continue;

                    bool iIsLow = waypoints[i].y < waypoints[next].y;
                    int lowIdx = iIsLow ? i : next;
                    int highIdx = iIsLow ? next : i;

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
        /// Try to lift the low-side node (and its neighbors away from the high side)
        /// up to elevated positions that maintain slopes &lt;= 26°.
        /// Returns true if propagation produced a valid chain.
        /// </summary>
        static bool TryPropagateUp(
            List<Vector3> waypoints, float[] groundY,
            int lowIdx, int highIdx, NavMeshQueryFilter filter)
        {
            int n = waypoints.Count;
            int direction = StepDirection(lowIdx, highIdx, n);

            // Walk from lowIdx away from highIdx, collecting candidates.
            var candidates = new List<(int idx, Vector3 pos)>();
            int cur = lowIdx;

            for (int step = 0; step < n; step++)
            {
                int toward = (cur - direction + n) % n; // neighbor toward high side
                float targetY = (candidates.Count > 0)
                    ? candidates[candidates.Count - 1].pos.y
                    : waypoints[highIdx].y;

                if (!TryFindElevatedPosition(waypoints[cur], targetY, filter, out Vector3 elevated))
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

                int nextCur = (cur + direction + n) % n;
                if (nextCur == highIdx) return false; // wrapped around
                cur = nextCur;
            }

            return false;
        }

        /// <summary>
        /// Try to find an elevated NavMesh position near the waypoint's XZ that keeps
        /// the slope to the target Y within limits.
        /// </summary>
        static bool TryFindElevatedPosition(
            Vector3 current, float targetY, NavMeshQueryFilter filter, out Vector3 result)
        {
            result = current;

            var probe = new Vector3(current.x, targetY, current.z);
            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit,
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
        /// Validate that all pairs in the candidate chain + existing waypoints
        /// have slopes &lt;= 26°.
        /// </summary>
        static bool ValidateChain(
            List<Vector3> waypoints,
            List<(int idx, Vector3 pos)> candidates,
            int highIdx)
        {
            if (candidates.Count == 0) return true;

            // Build a lookup of candidate positions.
            var posLookup = new Dictionary<int, Vector3>();
            foreach (var (idx, pos) in candidates)
                posLookup[idx] = pos;

            Vector3 Pos(int i) => posLookup.TryGetValue(i, out var p) ? p : waypoints[i];

            int n = waypoints.Count;

            // Check slope between consecutive candidates and between
            // the end candidates and their non-candidate neighbors.
            for (int c = 0; c < candidates.Count; c++)
            {
                int idx = candidates[c].idx;
                int prev = (idx - 1 + n) % n;
                int next = (idx + 1) % n;

                if (IsSteep(Pos(idx), Pos(prev))) return false;
                if (IsSteep(Pos(idx), Pos(next))) return false;
            }

            // Also check the transition to highIdx.
            if (IsSteep(Pos(candidates[0].idx), Pos(highIdx))) return false;

            return true;
        }

        /// <summary>
        /// Revert an elevated segment to ground-level positions. Starting from
        /// highIdx, walk through contiguous elevated nodes and re-sample them
        /// at ground level via NavMesh.
        /// </summary>
        static void RevertElevatedSegment(
            List<Vector3> waypoints, float[] groundY,
            int highIdx, NavMeshQueryFilter filter)
        {
            int n = waypoints.Count;
            float baseY = MinNeighborY(waypoints, highIdx);

            var toRevert = new List<int> { highIdx };

            // Expand in both directions to find the full elevated segment.
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int cur = (highIdx + dir + n) % n;
                int steps = 0;
                while (cur != highIdx && steps < n)
                {
                    float dy = Mathf.Abs(waypoints[cur].y - baseY);
                    if (dy < ElevationDeltaThreshold) break;
                    toRevert.Add(cur);
                    cur = (cur + dir + n) % n;
                    steps++;
                }
            }

            foreach (int idx in toRevert)
            {
                var wp = waypoints[idx];
                var probe = new Vector3(wp.x, baseY, wp.z);

                if (NavMesh.SamplePosition(probe, out NavMeshHit hit,
                        BoundaryGeometry.NavMeshProbeRadius, filter))
                {
                    waypoints[idx] = hit.position;
                    groundY[idx] = hit.position.y;
                }
            }
        }

        #endregion

        #region Helpers

        static bool IsSteep(Vector3 a, Vector3 b)
        {
            float dy = Mathf.Abs(a.y - b.y);
            float dxz = Vector2.Distance(
                new Vector2(a.x, a.z), new Vector2(b.x, b.z));

            if (dxz < 0.01f) return dy > 0.3f;
            return (dy / dxz) > MaxSlopeTan;
        }

        /// <summary>
        /// Determine which direction to step through the ring from lowIdx
        /// away from highIdx. Returns +1 or -1.
        /// </summary>
        static int StepDirection(int lowIdx, int highIdx, int n)
        {
            int fwd = (highIdx - lowIdx + n) % n;
            // Step in the direction away from highIdx.
            return fwd <= n / 2 ? -1 : 1;
        }

        static float MinNeighborY(List<Vector3> waypoints, int idx)
        {
            int n = waypoints.Count;
            float yPrev = waypoints[(idx - 1 + n) % n].y;
            float yNext = waypoints[(idx + 1) % n].y;
            return Mathf.Min(yPrev, yNext);
        }

        #endregion
    }
}
