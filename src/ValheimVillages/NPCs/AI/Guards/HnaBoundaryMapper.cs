using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.NPCs.AI.Guards
{
    /// <summary>
    /// Computes guard patrol waypoints from HNA region graph boundary cells.
    /// Pipeline: edge snap -> clockwise sort -> Chaikin smooth -> NavMesh re-snap -> RDP -> sharp angle prune.
    /// </summary>
    public static class HnaBoundaryMapper
    {
        private const float NavMeshProbeRadius = 4f;
        private static readonly float[] ElevationProbes = { 0f, 3f, 6f, 10f };
        private const float MaxEdgeXZDrift = 4f;

        /// <summary>When two waypoints are within this XZ radius of each other after
        /// edge-snapping, keep only the higher one (wall-top preference).
        /// Tuned via offline sweep: 3.0m with exterior-only boundary gives the cleanest
        /// patrol (14 WP, 0.72m mean, 100% coverage).</summary>
        private const float XZDedupeRadius = 3f;

        /// <summary>RDP epsilon: 1.0m aggressively simplifies now that boundary cells are
        /// exterior-only and free of inner noise. Preserves real geometry corners while
        /// producing a minimal waypoint set.</summary>
        private const float RdpEpsilon = 1f;

        /// <summary>Interior angle threshold for pruning. Waypoints with reflex angles above
        /// this are removed if the neighbors have a clear NavMesh path between them.
        /// 270° only prunes near-complete U-turns.</summary>
        private const float SharpAngleThreshold = 270f;


        /// <summary>
        /// Compute patrol waypoints from HNA boundary cells, sorted clockwise around the bed.
        /// Returns an empty list if the HNA graph is unavailable or has fewer than 3 boundary cells.
        /// </summary>
        public static List<Vector3> ComputeBoundaryWaypoints(Vector3 bedPosition)
        {
            if (!HnaRegionGraph.IsAvailable)
                return new List<Vector3>();

            var boundaryCells = HnaRegionGraph.GetBoundaryCells();
            if (boundaryCells.Count < 3)
                return new List<Vector3>();

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas
            };

            // Step 1: Edge-snap each boundary cell to the nearest NavMesh edge.
            // Probe from the outermost corner of the cell (offset by outward direction)
            // so FindClosestEdge favors the exterior perimeter over interior balcony edges.
            var waypoints = new List<Vector3>();
            int elevated = 0;
            foreach (var (_, cellCenter, outwardDir) in boundaryCells)
            {
                float halfCell = HnaRegionGraph.CellSize * 0.5f;
                var probeOrigin = cellCenter + outwardDir * halfCell;

                if (TryFindBestEdge(probeOrigin, cellCenter, filter, out var edgePos, out bool isElevated))
                {
                    waypoints.Add(edgePos);
                    if (isElevated) elevated++;
                }
            }

            if (waypoints.Count < 3)
            {
                Plugin.Log?.LogWarning(
                    $"[HnaBoundary] Edge snap produced only {waypoints.Count} waypoints, " +
                    $"falling back to raw boundary cells");
                return FallbackFromCells(boundaryCells.ConvertAll(c => (c.cellId, c.worldCenter)),
                    bedPosition);
            }

            // Step 1b: Deduplicate waypoints close in XZ, keeping the higher Y (wall-top preference).
            int preDedupe = waypoints.Count;
            waypoints = DeduplicateByXZ(waypoints);
            int postDedupe = waypoints.Count;

            // Step 2: Sort clockwise
            SortClockwise(waypoints, bedPosition);
            int edgeCount = waypoints.Count;

            // Step 3: Light Chaikin smoothing to soften grid staircase
            waypoints = ChaikinSmooth(waypoints);

            // Step 4: Re-snap Chaikin points to NavMesh (interpolated, may be off-mesh)
            waypoints = NavMeshReSnap(waypoints, filter);
            int smoothedCount = waypoints.Count;

            // Step 5: RDP to prune grid noise while preserving real geometry corners
            waypoints = SimplifyRDP(waypoints, RdpEpsilon);
            int rdpCount = waypoints.Count;

            // Step 6: Prune sharp reflex angles with clear NavMesh paths
            int pruned = PruneSharpAngles(waypoints, filter);

            // Step 7: Final XZ dedup
            int preFinalDedup = waypoints.Count;
            waypoints = DeduplicateByXZ(waypoints);

            // Step 8: Enforce monotonic clockwise angle — remove any waypoint that
            // backtracks in angle from the bed center rather than progressing around the perimeter.
            int preMonotonic = waypoints.Count;
            waypoints = EnforceMonotonicAngle(waypoints, bedPosition);
            int monotonicDrops = preMonotonic - waypoints.Count;

            // Step 9: Remove fully-isolated waypoints (both connections fail)
            int transitionDrops = RemoveUnpathableTransitions(waypoints, filter);

            // Step 10: Snap elevation transitions to nearest HNA link (stair/door) endpoints.
            // Where consecutive waypoints have a height change, insert the link's start/end
            // positions so the guard uses stairs rather than walking off edges.
            int linksInserted = InsertLinkWaypoints(waypoints, filter);

            Plugin.Log?.LogInfo(
                $"[HnaBoundary] {preDedupe} edge -> {postDedupe} dedup -> " +
                $"{edgeCount} sorted -> {smoothedCount} Chaikin -> {rdpCount} RDP -> " +
                $"{preFinalDedup} pruned -> {preMonotonic} dedup -> {waypoints.Count} final " +
                $"({pruned} sharp, {monotonicDrops} backtrack, {transitionDrops} unpathable, " +
                $"{linksInserted} links, {elevated} elevated, {HnaRegionGraph.RegionCount} HNA regions)");

            return waypoints;
        }

        #region Diagnostics

        /// <summary>
        /// Run only the edge-snap step (Step 1) and return raw results for offline testing.
        /// Each entry contains the boundary cell center, outward direction, and the
        /// edge-snapped position (or null if snapping failed).
        /// </summary>
        public static List<(Vector3 cellCenter, Vector3 outwardDir, Vector3? edgeSnapped, bool elevated)>
            DiagnosticEdgeSnap()
        {
            var results = new List<(Vector3, Vector3, Vector3?, bool)>();
            if (!HnaRegionGraph.IsAvailable) return results;

            var boundaryCells = HnaRegionGraph.GetBoundaryCells();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas
            };

            foreach (var (_, cellCenter, outwardDir) in boundaryCells)
            {
                float halfCell = HnaRegionGraph.CellSize * 0.5f;
                var probeOrigin = cellCenter + outwardDir * halfCell;

                if (TryFindBestEdge(probeOrigin, cellCenter, filter, out var edgePos, out bool isElevated))
                    results.Add((cellCenter, outwardDir, edgePos, isElevated));
                else
                    results.Add((cellCenter, outwardDir, null, false));
            }

            return results;
        }

        #endregion

        #region NavMesh Edge Snapping

        /// <param name="probeOrigin">
        /// Outward-offset position (cell center + outward * halfCell).
        /// Biases FindClosestEdge toward the exterior perimeter.
        /// </param>
        /// <param name="cellCenter">Original cell center for XZ-drift check.</param>
        private static bool TryFindBestEdge(
            Vector3 probeOrigin, Vector3 cellCenter, NavMeshQueryFilter filter,
            out Vector3 bestEdge, out bool isElevated)
        {
            bestEdge = cellCenter;
            isElevated = false;
            bool found = false;
            Vector3 groundEdge = default;
            bool hasGround = false;

            foreach (float heightOffset in ElevationProbes)
            {
                var probe = new Vector3(probeOrigin.x, probeOrigin.y + heightOffset, probeOrigin.z);

                if (!NavMesh.SamplePosition(probe, out NavMeshHit sample, NavMeshProbeRadius, filter))
                    continue;

                if (!NavMesh.FindClosestEdge(sample.position, out NavMeshHit edge, filter))
                    continue;

                // Drift measured from the original cell center, not the offset probe
                float xzDrift = Vector2.Distance(
                    new Vector2(cellCenter.x, cellCenter.z),
                    new Vector2(edge.position.x, edge.position.z));

                if (xzDrift > MaxEdgeXZDrift)
                    continue;

                if (heightOffset == 0f)
                {
                    groundEdge = edge.position;
                    hasGround = true;
                    bestEdge = edge.position;
                    found = true;
                }
                else if (hasGround)
                {
                    if (NavMesh.Raycast(groundEdge, edge.position, out _, filter))
                        continue;

                    bestEdge = edge.position;
                    isElevated = true;
                }
                else
                {
                    bestEdge = edge.position;
                    isElevated = true;
                    found = true;
                }
            }

            return found;
        }

        #endregion

        #region Chaikin Smoothing

        /// <summary>
        /// Chaikin corner-cutting: for each edge A->B in the closed polygon, generate two
        /// new points at 25% and 75% along the segment. This rounds staircase corners into
        /// smooth curves. One pass doubles point count but eliminates grid-aligned jaggedness.
        /// </summary>
        private static List<Vector3> ChaikinSmooth(List<Vector3> points)
        {
            if (points.Count < 3) return points;

            int n = points.Count;
            var result = new List<Vector3>(n * 2);

            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                var a = points[i];
                var b = points[next];

                result.Add(Vector3.Lerp(a, b, 0.25f));
                result.Add(Vector3.Lerp(a, b, 0.75f));
            }

            return result;
        }

        /// <summary>
        /// Re-snap interpolated Chaikin points back onto the NavMesh.
        /// Points that can't be snapped are dropped.
        /// </summary>
        private static List<Vector3> NavMeshReSnap(List<Vector3> points, NavMeshQueryFilter filter)
        {
            var result = new List<Vector3>(points.Count);
            foreach (var p in points)
            {
                if (NavMesh.SamplePosition(p, out NavMeshHit hit, NavMeshProbeRadius, filter))
                    result.Add(hit.position);
            }
            return result;
        }

        #endregion

        #region Sharp Angle Pruning

        private static int PruneSharpAngles(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            if (waypoints.Count <= 4) return 0;

            int pruned = 0;
            for (int i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                int prev = (i - 1 + waypoints.Count) % waypoints.Count;
                int next = (i + 1) % waypoints.Count;

                float angle = InteriorAngleCW(waypoints[prev], waypoints[i], waypoints[next]);
                if (angle < SharpAngleThreshold)
                    continue;

                if (!IsNavMeshPathClear(waypoints[prev], waypoints[next], filter))
                    continue;

                waypoints.RemoveAt(i);
                pruned++;
            }

            return pruned;
        }

        /// <summary>
        /// Walk the clockwise-sorted waypoint ring and check consecutive pathability.
        /// Only drop a waypoint if BOTH prev→curr AND curr→next paths fail (fully isolated).
        /// If at least one direction works, the waypoint serves as an elevation bridge
        /// (e.g. stair landing) and must be preserved. The guard's forward-recovery handles
        /// any remaining broken transitions at runtime.
        /// </summary>
        private static int RemoveUnpathableTransitions(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            if (waypoints.Count <= 4) return 0;

            int dropped = 0;
            for (int i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                int prev = (i - 1 + waypoints.Count) % waypoints.Count;
                int next = (i + 1) % waypoints.Count;

                bool prevOk = IsNavMeshPathClear(waypoints[prev], waypoints[i], filter);
                bool nextOk = IsNavMeshPathClear(waypoints[i], waypoints[next], filter);

                // Keep if at least one connection works — it's a bridge or partial segment
                if (prevOk || nextOk) continue;

                // Both directions fail — try re-snapping at the average Y of neighbors
                float neighborY = (waypoints[prev].y + waypoints[next].y) * 0.5f;
                var resnap = new Vector3(waypoints[i].x, neighborY, waypoints[i].z);
                if (NavMesh.SamplePosition(resnap, out NavMeshHit hit, NavMeshProbeRadius, filter))
                {
                    bool prevOk2 = IsNavMeshPathClear(waypoints[prev], hit.position, filter);
                    bool nextOk2 = IsNavMeshPathClear(hit.position, waypoints[next], filter);
                    if (prevOk2 || nextOk2)
                    {
                        waypoints[i] = hit.position;
                        continue;
                    }
                }

                // Fully isolated — safe to drop if neighbors can reach each other
                if (IsNavMeshPathClear(waypoints[prev], waypoints[next], filter))
                {
                    waypoints.RemoveAt(i);
                    dropped++;
                }
            }

            return dropped;
        }

        private static float InteriorAngleCW(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = new Vector2(b.x - a.x, b.z - a.z);
            var bc = new Vector2(c.x - b.x, c.z - b.z);

            float cross = ab.x * bc.y - ab.y * bc.x;
            float dot = ab.x * bc.x + ab.y * bc.y;
            float turnAngle = Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;

            float interior = 180f - turnAngle;
            if (interior < 0f) interior += 360f;
            if (interior >= 360f) interior -= 360f;
            return interior;
        }

        /// <summary>
        /// Check if a full NavMesh path exists between two points. Uses CalculatePath
        /// (not Raycast) so it respects doors, bridges, stairs, and NavMesh links.
        /// </summary>
        private static bool IsNavMeshPathClear(Vector3 from, Vector3 to, NavMeshQueryFilter filter)
        {
            if (!NavMesh.SamplePosition(from, out NavMeshHit srcHit, NavMeshProbeRadius, filter))
                return false;
            if (!NavMesh.SamplePosition(to, out NavMeshHit dstHit, NavMeshProbeRadius, filter))
                return false;

            var path = new NavMeshPath();
            NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, path);
            return path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete;
        }

        #endregion

        #region RDP Simplification

        private static List<Vector3> SimplifyRDP(List<Vector3> points, float epsilon)
        {
            if (points.Count <= 3) return points;

            int oppositeIdx = points.Count / 2;

            var firstHalf = new List<Vector3>();
            for (int i = 0; i <= oppositeIdx; i++)
                firstHalf.Add(points[i]);

            var secondHalf = new List<Vector3>();
            for (int i = oppositeIdx; i < points.Count; i++)
                secondHalf.Add(points[i]);
            secondHalf.Add(points[0]);

            var simplifiedFirst = RDPRecursive(firstHalf, epsilon);
            var simplifiedSecond = RDPRecursive(secondHalf, epsilon);

            var result = new List<Vector3>(simplifiedFirst);
            for (int i = 1; i < simplifiedSecond.Count - 1; i++)
                result.Add(simplifiedSecond[i]);

            return result.Count >= 3 ? result : points;
        }

        private static List<Vector3> RDPRecursive(List<Vector3> points, float epsilon)
        {
            if (points.Count <= 2) return new List<Vector3>(points);

            float maxDist = 0f;
            int maxIdx = 0;
            var lineStart = new Vector2(points[0].x, points[0].z);
            var lineEnd = new Vector2(points[points.Count - 1].x, points[points.Count - 1].z);

            for (int i = 1; i < points.Count - 1; i++)
            {
                float dist = PerpendicularDistance2D(
                    new Vector2(points[i].x, points[i].z), lineStart, lineEnd);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIdx = i;
                }
            }

            if (maxDist > epsilon)
            {
                var left = new List<Vector3>();
                for (int i = 0; i <= maxIdx; i++) left.Add(points[i]);
                var right = new List<Vector3>();
                for (int i = maxIdx; i < points.Count; i++) right.Add(points[i]);

                var simplifiedLeft = RDPRecursive(left, epsilon);
                var simplifiedRight = RDPRecursive(right, epsilon);

                var result = new List<Vector3>(simplifiedLeft);
                for (int i = 1; i < simplifiedRight.Count; i++)
                    result.Add(simplifiedRight[i]);
                return result;
            }

            return new List<Vector3> { points[0], points[points.Count - 1] };
        }

        private static float PerpendicularDistance2D(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
        {
            var line = lineEnd - lineStart;
            float lineLenSq = line.sqrMagnitude;
            if (lineLenSq < 0.0001f) return Vector2.Distance(point, lineStart);

            float t = Mathf.Clamp01(Vector2.Dot(point - lineStart, line) / lineLenSq);
            var projection = lineStart + t * line;
            return Vector2.Distance(point, projection);
        }

        #endregion

        #region Link Snapping

        /// <summary>Minimum height difference between consecutive waypoints to trigger link insertion.</summary>
        private const float LinkElevationThreshold = 2f;

        /// <summary>Max XZ distance from a waypoint midpoint to consider an HNA link relevant.</summary>
        private const float LinkSearchRadius = 10f;

        /// <summary>
        /// For consecutive waypoint pairs with significant elevation change, find the nearest
        /// HNA stair/door link and insert its start/end positions between the waypoints.
        /// This guides the guard through stairs rather than off ledges.
        /// </summary>
        private static int InsertLinkWaypoints(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            var links = HnaRegionGraph.GetAllLinks();
            if (links == null || links.Count == 0) return 0;

            int inserted = 0;
            for (int i = 0; i < waypoints.Count; i++)
            {
                int next = (i + 1) % waypoints.Count;
                float dy = Mathf.Abs(waypoints[next].y - waypoints[i].y);
                if (dy < LinkElevationThreshold) continue;

                var midpoint = (waypoints[i] + waypoints[next]) * 0.5f;
                HnaLink? best = null;
                float bestDist = float.MaxValue;

                foreach (var link in links)
                {
                    var linkMid = (link.PositionStart + link.PositionEnd) * 0.5f;
                    float xzDist = Vector2.Distance(
                        new Vector2(midpoint.x, midpoint.z),
                        new Vector2(linkMid.x, linkMid.z));

                    if (xzDist > LinkSearchRadius) continue;
                    if (xzDist < bestDist)
                    {
                        bestDist = xzDist;
                        best = link;
                    }
                }

                if (best == null) continue;

                // Orient link endpoints: lower one first if going down, higher one first if going up
                var linkVal = best.Value;
                Vector3 linkA, linkB;
                if (waypoints[i].y > waypoints[next].y)
                {
                    // Going down: high end first, low end second
                    linkA = linkVal.PositionStart.y > linkVal.PositionEnd.y
                        ? linkVal.PositionStart : linkVal.PositionEnd;
                    linkB = linkVal.PositionStart.y > linkVal.PositionEnd.y
                        ? linkVal.PositionEnd : linkVal.PositionStart;
                }
                else
                {
                    // Going up: low end first, high end second
                    linkA = linkVal.PositionStart.y < linkVal.PositionEnd.y
                        ? linkVal.PositionStart : linkVal.PositionEnd;
                    linkB = linkVal.PositionStart.y < linkVal.PositionEnd.y
                        ? linkVal.PositionEnd : linkVal.PositionStart;
                }

                // Only insert if the link points are reachable from the surrounding waypoints
                if (!IsNavMeshPathClear(waypoints[i], linkA, filter)) continue;
                if (!IsNavMeshPathClear(linkB, waypoints[next], filter)) continue;

                // Insert link endpoints after waypoint[i]
                waypoints.Insert(i + 1, linkA);
                waypoints.Insert(i + 2, linkB);
                inserted += 2;
                i += 2; // skip past the inserted points
            }

            return inserted;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Remove waypoints that are within XZDedupeRadius in the XZ plane of another waypoint,
        /// keeping the one with the higher Y (wall-top preference). O(n²) but n is small (~30-60).
        /// </summary>
        private static List<Vector3> DeduplicateByXZ(List<Vector3> waypoints)
        {
            float radiusSq = XZDedupeRadius * XZDedupeRadius;
            var keep = new bool[waypoints.Count];
            for (int i = 0; i < keep.Length; i++) keep[i] = true;

            for (int i = 0; i < waypoints.Count; i++)
            {
                if (!keep[i]) continue;
                for (int j = i + 1; j < waypoints.Count; j++)
                {
                    if (!keep[j]) continue;

                    float dx = waypoints[i].x - waypoints[j].x;
                    float dz = waypoints[i].z - waypoints[j].z;
                    if (dx * dx + dz * dz > radiusSq) continue;

                    // Keep the higher one
                    if (waypoints[j].y > waypoints[i].y)
                        keep[i] = false;
                    else
                        keep[j] = false;
                }
            }

            var result = new List<Vector3>();
            for (int i = 0; i < waypoints.Count; i++)
                if (keep[i]) result.Add(waypoints[i]);
            return result;
        }

        private static List<Vector3> FallbackFromCells(
            List<(string cellId, Vector3 worldCenter)> boundaryCells, Vector3 bedPosition)
        {
            var waypoints = new List<Vector3>(boundaryCells.Count);
            foreach (var (_, worldCenter) in boundaryCells)
                waypoints.Add(worldCenter);
            SortClockwise(waypoints, bedPosition);
            return waypoints;
        }

        private static void SortClockwise(List<Vector3> waypoints, Vector3 center)
        {
            waypoints.Sort((a, b) =>
            {
                float ax = a.x - center.x;
                float az = a.z - center.z;
                float bx = b.x - center.x;
                float bz = b.z - center.z;
                float angleA = Mathf.Atan2(az, ax);
                float angleB = Mathf.Atan2(bz, bx);
                return angleB.CompareTo(angleA);
            });
        }

        /// <summary>
        /// Walk the clockwise-sorted waypoint list and drop any waypoint whose angle
        /// from the bed center doesn't advance (decrease) relative to the previous kept
        /// waypoint. This eliminates backtracks caused by Chaikin/RDP/dedup shifting
        /// points out of strict angular order.
        /// </summary>
        private static List<Vector3> EnforceMonotonicAngle(List<Vector3> waypoints, Vector3 center)
        {
            if (waypoints.Count < 3) return waypoints;

            var result = new List<Vector3>(waypoints.Count) { waypoints[0] };
            float prevAngle = Mathf.Atan2(waypoints[0].z - center.z, waypoints[0].x - center.x);

            for (int i = 1; i < waypoints.Count; i++)
            {
                float angle = Mathf.Atan2(waypoints[i].z - center.z, waypoints[i].x - center.x);

                // Clockwise = descending angle. Compute signed delta; negative means advancing.
                float delta = angle - prevAngle;

                // Normalize to (-π, π]
                if (delta > Mathf.PI) delta -= 2f * Mathf.PI;
                if (delta <= -Mathf.PI) delta += 2f * Mathf.PI;

                // Keep only if the angle advanced clockwise (delta < 0) or is very small
                // (within ~2° tolerance for nearly-tangential points)
                if (delta < 0.035f) // ~2° tolerance
                {
                    result.Add(waypoints[i]);
                    prevAngle = angle;
                }
            }

            return result.Count >= 3 ? result : waypoints;
        }

        #endregion
    }
}
