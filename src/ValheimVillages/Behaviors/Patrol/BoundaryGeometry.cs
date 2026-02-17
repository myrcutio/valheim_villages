using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Pure geometry operations for boundary waypoint computation:
    /// edge snapping, Chaikin smoothing, RDP simplification, clockwise sorting,
    /// XZ deduplication, sharp-angle pruning, and monotonic-angle enforcement.
    /// Extracted from HnaBoundaryMapper to keep the orchestration pipeline thin.
    /// </summary>
    internal static class BoundaryGeometry
    {
        internal const float NavMeshProbeRadius = 4f;
        private static readonly float[] ElevationProbes = { 0f, 3f, 6f, 10f };
        private const float MaxEdgeXZDrift = 4f;

        /// <summary>When two waypoints are within this XZ radius of each other after
        /// edge-snapping, keep only the higher one (wall-top preference).</summary>
        internal const float XZDedupeRadius = 3f;

        /// <summary>RDP epsilon: 1.0m aggressively simplifies now that boundary cells are
        /// exterior-only and free of inner noise.</summary>
        internal const float RdpEpsilon = 1f;

        /// <summary>Interior angle threshold for pruning. Waypoints with reflex angles above
        /// this are removed if the neighbors have a clear NavMesh path between them.</summary>
        internal const float SharpAngleThreshold = 270f;

        #region NavMesh Edge Snapping

        /// <param name="probeOrigin">
        /// Outward-offset position (cell center + outward * halfCell).
        /// Biases FindClosestEdge toward the exterior perimeter.
        /// </param>
        /// <param name="cellCenter">Original cell center for XZ-drift check.</param>
        internal static bool TryFindBestEdge(
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
        /// new points at 25% and 75% along the segment.
        /// </summary>
        internal static List<Vector3> ChaikinSmooth(List<Vector3> points)
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
        internal static List<Vector3> NavMeshReSnap(List<Vector3> points, NavMeshQueryFilter filter)
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

        internal static int PruneSharpAngles(List<Vector3> waypoints, NavMeshQueryFilter filter)
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

        internal static float InteriorAngleCW(Vector3 a, Vector3 b, Vector3 c)
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
        /// Check if a full NavMesh path exists between two points.
        /// </summary>
        internal static bool IsNavMeshPathClear(Vector3 from, Vector3 to, NavMeshQueryFilter filter)
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

        internal static List<Vector3> SimplifyRDP(List<Vector3> points, float epsilon)
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

        #region Helpers

        /// <summary>
        /// Remove waypoints that are within XZDedupeRadius in the XZ plane of another waypoint,
        /// keeping the one with the higher Y (wall-top preference).
        /// </summary>
        internal static List<Vector3> DeduplicateByXZ(List<Vector3> waypoints)
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

        internal static List<Vector3> FallbackFromCells(
            List<(string cellId, Vector3 worldCenter)> boundaryCells, Vector3 bedPosition)
        {
            var waypoints = new List<Vector3>(boundaryCells.Count);
            foreach (var (_, worldCenter) in boundaryCells)
                waypoints.Add(worldCenter);
            SortClockwise(waypoints, bedPosition);
            return waypoints;
        }

        internal static void SortClockwise(List<Vector3> waypoints, Vector3 center)
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
        /// waypoint.
        /// </summary>
        internal static List<Vector3> EnforceMonotonicAngle(List<Vector3> waypoints, Vector3 center)
        {
            if (waypoints.Count < 3) return waypoints;

            var result = new List<Vector3>(waypoints.Count) { waypoints[0] };
            float prevAngle = Mathf.Atan2(waypoints[0].z - center.z, waypoints[0].x - center.x);

            for (int i = 1; i < waypoints.Count; i++)
            {
                float angle = Mathf.Atan2(waypoints[i].z - center.z, waypoints[i].x - center.x);

                float delta = angle - prevAngle;

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
