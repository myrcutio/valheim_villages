using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Pure geometry operations for boundary waypoint computation:
    ///     edge snapping, Chaikin smoothing, RDP simplification, clockwise sorting,
    ///     XZ deduplication, sharp-angle pruning, and monotonic-angle enforcement.
    ///     Extracted from BoundaryMapper to keep the orchestration pipeline thin.
    /// </summary>
    internal static class BoundaryGeometry
    {
        internal const float NavMeshProbeRadius = 4f;
        private const float MaxEdgeXZDrift = 4f;

        /// <summary>
        ///     When two waypoints are within this XZ radius of each other after
        ///     edge-snapping, keep only the higher one (wall-top preference).
        ///     Must be smaller than CellSize (3m) to avoid merging adjacent boundary cells.
        /// </summary>
        internal const float XZDedupeRadius = 1.5f;

        /// <summary>
        ///     RDP epsilon: 1.0m aggressively simplifies now that boundary cells are
        ///     exterior-only and free of inner noise.
        /// </summary>
        internal const float RdpEpsilon = 1f;

        /// <summary>
        ///     Interior angle threshold for pruning. Waypoints with reflex angles above
        ///     this are removed if the neighbors have a clear NavMesh path between them.
        /// </summary>
        internal const float SharpAngleThreshold = 270f;

        private static readonly float[] ElevationProbes = { 0f, 3f, 6f, 10f };

        #region NavMesh Edge Snapping

        /// <param name="probeOrigin">
        ///     Outward-offset position (cell center + outward * halfCell).
        ///     Biases FindClosestEdge toward the exterior perimeter.
        /// </param>
        /// <param name="cellCenter">Original cell center for XZ-drift check.</param>
        internal static bool TryFindBestEdge(
            Vector3 probeOrigin, Vector3 cellCenter, NavMeshQueryFilter filter,
            out Vector3 bestEdge, out bool isElevated)
        {
            bestEdge = cellCenter;
            isElevated = false;
            var found = false;
            Vector3 groundEdge = default;
            var hasGround = false;

            foreach (var heightOffset in ElevationProbes)
            {
                var probe = new Vector3(probeOrigin.x, probeOrigin.y + heightOffset, probeOrigin.z);

                if (!NavMesh.SamplePosition(probe, out var sample, NavMeshProbeRadius, filter))
                    continue;

                if (!NavMesh.FindClosestEdge(sample.position, out var edge, filter))
                    continue;

                var xzDrift = Vector2.Distance(
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

        #region RDP Simplification

        private static float PerpendicularDistance2D(Vector2 point, Vector2 lineStart, Vector2 lineEnd)
        {
            var line = lineEnd - lineStart;
            var lineLenSq = line.sqrMagnitude;
            if (lineLenSq < 0.0001f) return Vector2.Distance(point, lineStart);

            var t = Mathf.Clamp01(Vector2.Dot(point - lineStart, line) / lineLenSq);
            var projection = lineStart + t * line;
            return Vector2.Distance(point, projection);
        }

        #endregion

        #region Chaikin Smoothing

        /// <summary>
        ///     Chaikin corner-cutting: for each edge A->B in the closed polygon, generate two
        ///     new points at 25% and 75% along the segment.
        /// </summary>
        internal static List<Vector3> ChaikinSmooth(List<Vector3> points)
        {
            if (points.Count < 3) return points;

            var n = points.Count;
            var result = new List<Vector3>(n * 2);

            for (var i = 0; i < n; i++)
            {
                var next = (i + 1) % n;
                var a = points[i];
                var b = points[next];

                result.Add(Vector3.Lerp(a, b, 0.25f));
                result.Add(Vector3.Lerp(a, b, 0.75f));
            }

            return result;
        }

        /// <summary>
        ///     Re-snap interpolated Chaikin points back onto the NavMesh.
        ///     Points that can't be snapped are dropped.
        /// </summary>
        internal static List<Vector3> NavMeshReSnap(List<Vector3> points, NavMeshQueryFilter filter)
        {
            var result = new List<Vector3>(points.Count);
            foreach (var p in points)
                if (NavMesh.SamplePosition(p, out var hit, NavMeshProbeRadius, filter))
                    result.Add(hit.position);
            return result;
        }

        #endregion

        #region Sharp Angle Pruning

        internal static int PruneSharpAngles(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            if (waypoints.Count <= 4) return 0;

            var pruned = 0;
            for (var i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                var prev = (i - 1 + waypoints.Count) % waypoints.Count;
                var next = (i + 1) % waypoints.Count;

                var angle = InteriorAngleCW(waypoints[prev], waypoints[i], waypoints[next]);
                if (angle < SharpAngleThreshold)
                    continue;

                if (!IsNavMeshPathClear(waypoints[prev], waypoints[next], filter))
                    continue;

                waypoints.RemoveAt(i);
                pruned++;
            }

            return pruned;
        }

        internal static float InteriorAngleCW(Vector3 a, Vector3 b, Vector3 c)
        {
            var ab = new Vector2(b.x - a.x, b.z - a.z);
            var bc = new Vector2(c.x - b.x, c.z - b.z);

            var cross = ab.x * bc.y - ab.y * bc.x;
            var dot = ab.x * bc.x + ab.y * bc.y;
            var turnAngle = Mathf.Atan2(cross, dot) * Mathf.Rad2Deg;

            var interior = 180f - turnAngle;
            if (interior < 0f) interior += 360f;
            if (interior >= 360f) interior -= 360f;
            return interior;
        }

        /// <summary>
        ///     Check if a full NavMesh path exists between two points.
        /// </summary>
        internal static bool IsNavMeshPathClear(Vector3 from, Vector3 to, NavMeshQueryFilter filter)
        {
            if (!NavMesh.SamplePosition(from, out var srcHit, NavMeshProbeRadius, filter))
                return false;
            if (!NavMesh.SamplePosition(to, out var dstHit, NavMeshProbeRadius, filter))
                return false;

            var path = new NavMeshPath();
            NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, path);
            return path.status == NavMeshPathStatus.PathComplete;
        }

        #endregion

        #region Helpers

        internal static void SortClockwise(List<Vector3> waypoints, Vector3 center)
        {
            waypoints.Sort((a, b) =>
            {
                var ax = a.x - center.x;
                var az = a.z - center.z;
                var bx = b.x - center.x;
                var bz = b.z - center.z;
                var angleA = Mathf.Atan2(az, ax);
                var angleB = Mathf.Atan2(bz, bx);
                return angleB.CompareTo(angleA);
            });
        }

        /// <summary>
        ///     Walk the clockwise-sorted waypoint list and drop any waypoint whose angle
        ///     from the bed center doesn't advance (decrease) relative to the previous kept
        ///     waypoint.
        /// </summary>
        internal static List<Vector3> EnforceMonotonicAngle(List<Vector3> waypoints, Vector3 center)
        {
            if (waypoints.Count < 3) return waypoints;

            var result = new List<Vector3>(waypoints.Count) { waypoints[0] };
            var prevAngle = Mathf.Atan2(waypoints[0].z - center.z, waypoints[0].x - center.x);

            for (var i = 1; i < waypoints.Count; i++)
            {
                var angle = Mathf.Atan2(waypoints[i].z - center.z, waypoints[i].x - center.x);

                var delta = angle - prevAngle;

                if (delta > Mathf.PI) delta -= 2f * Mathf.PI;
                if (delta <= -Mathf.PI) delta += 2f * Mathf.PI;

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