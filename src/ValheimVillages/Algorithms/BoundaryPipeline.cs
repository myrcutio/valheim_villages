using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Algorithms
{
    /// <summary>
    ///     Offline reimplementation of BoundaryMapper's pure-geometry pipeline steps.
    ///     NavMesh-dependent steps (re-snap, unpathable removal) are skipped; sharp angle
    ///     pruning uses straight-line distance as a proxy for NavMesh pathability.
    /// </summary>
    public static class BoundaryPipeline
    {
        /// <summary>
        ///     Run the full offline pipeline on edge-snapped positions, returning the final waypoints.
        /// </summary>
        public static List<Vector3> Run(List<Vector3> edgeSnapped, Vector3 bedCenter, PipelineParams p)
        {
            if (edgeSnapped.Count < 3) return new List<Vector3>(edgeSnapped);

            var waypoints = DeduplicateByXZ(edgeSnapped, p.XzDedupeRadius);
            SortClockwise(waypoints, bedCenter);

            if (p.ChaikinEnabled)
                waypoints = ChaikinSmooth(waypoints);

            waypoints = SimplifyRDP(waypoints, p.RdpEpsilon);
            PruneSharpAngles(waypoints, p.SharpAngleThreshold);
            waypoints = DeduplicateByXZ(waypoints, p.XzDedupeRadius);
            waypoints = EnforceMonotonicAngle(waypoints, bedCenter);

            return waypoints;
        }

        #region Sort

        public static void SortClockwise(List<Vector3> waypoints, Vector3 center)
        {
            waypoints.Sort((a, b) =>
            {
                var angleA = (float)Math.Atan2(a.z - center.z, a.x - center.x);
                var angleB = (float)Math.Atan2(b.z - center.z, b.x - center.x);
                return angleB.CompareTo(angleA);
            });
        }

        #endregion

        #region Chaikin

        public static List<Vector3> ChaikinSmooth(List<Vector3> points)
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

        #endregion

        #region Monotonic Angle

        public static List<Vector3> EnforceMonotonicAngle(List<Vector3> waypoints, Vector3 center)
        {
            if (waypoints.Count < 3) return waypoints;

            var result = new List<Vector3>(waypoints.Count) { waypoints[0] };
            var prevAngle = (float)Math.Atan2(waypoints[0].z - center.z, waypoints[0].x - center.x);

            for (var i = 1; i < waypoints.Count; i++)
            {
                var angle = (float)Math.Atan2(waypoints[i].z - center.z, waypoints[i].x - center.x);
                var delta = angle - prevAngle;

                if (delta > (float)Math.PI) delta -= 2f * (float)Math.PI;
                if (delta <= -(float)Math.PI) delta += 2f * (float)Math.PI;

                if (delta < 0.035f) // ~2° tolerance
                {
                    result.Add(waypoints[i]);
                    prevAngle = angle;
                }
            }

            return result.Count >= 3 ? result : waypoints;
        }

        #endregion

        #region XZ Dedup

        public static List<Vector3> DeduplicateByXZ(List<Vector3> waypoints, float radius)
        {
            var radiusSq = radius * radius;
            var keep = new bool[waypoints.Count];
            for (var k = 0; k < keep.Length; k++) keep[k] = true;

            for (var i = 0; i < waypoints.Count; i++)
            {
                if (!keep[i]) continue;
                for (var j = i + 1; j < waypoints.Count; j++)
                {
                    if (!keep[j]) continue;
                    var dx = waypoints[i].x - waypoints[j].x;
                    var dz = waypoints[i].z - waypoints[j].z;
                    if (dx * dx + dz * dz > radiusSq) continue;

                    if (waypoints[j].y > waypoints[i].y)
                        keep[i] = false;
                    else
                        keep[j] = false;
                }
            }

            var result = new List<Vector3>();
            for (var i = 0; i < waypoints.Count; i++)
                if (keep[i])
                    result.Add(waypoints[i]);
            return result;
        }

        #endregion

        public class PipelineParams
        {
            public PipelineParams(float rdpEpsilon, float sharpAngleThreshold,
                bool chaikinEnabled, float xzDedupeRadius)
            {
                RdpEpsilon = rdpEpsilon;
                SharpAngleThreshold = sharpAngleThreshold;
                ChaikinEnabled = chaikinEnabled;
                XzDedupeRadius = xzDedupeRadius;
            }

            public float RdpEpsilon { get; }
            public float SharpAngleThreshold { get; }
            public bool ChaikinEnabled { get; }
            public float XzDedupeRadius { get; }
        }

        #region RDP

        public static List<Vector3> SimplifyRDP(List<Vector3> points, float epsilon)
        {
            if (points.Count <= 3) return points;

            var oppositeIdx = points.Count / 2;
            var firstHalf = points.GetRange(0, oppositeIdx + 1);
            var secondHalf = points.GetRange(oppositeIdx, points.Count - oppositeIdx);
            secondHalf.Add(points[0]);

            var simpFirst = RDPRecursive(firstHalf, epsilon);
            var simpSecond = RDPRecursive(secondHalf, epsilon);

            var result = new List<Vector3>(simpFirst);
            for (var i = 1; i < simpSecond.Count - 1; i++)
                result.Add(simpSecond[i]);

            return result.Count >= 3 ? result : points;
        }

        private static List<Vector3> RDPRecursive(List<Vector3> points, float epsilon)
        {
            if (points.Count <= 2) return new List<Vector3>(points);

            var maxDist = 0f;
            var maxIdx = 0;
            var start = points[0];
            var end = points[points.Count - 1];

            for (var i = 1; i < points.Count - 1; i++)
            {
                var dist = PerpendicularDistance2D(points[i], start, end);
                if (dist > maxDist)
                {
                    maxDist = dist;
                    maxIdx = i;
                }
            }

            if (maxDist > epsilon)
            {
                var left = points.GetRange(0, maxIdx + 1);
                var right = points.GetRange(maxIdx, points.Count - maxIdx);
                var simpLeft = RDPRecursive(left, epsilon);
                var simpRight = RDPRecursive(right, epsilon);
                var result = new List<Vector3>(simpLeft);
                for (var i = 1; i < simpRight.Count; i++)
                    result.Add(simpRight[i]);
                return result;
            }

            return new List<Vector3> { points[0], points[points.Count - 1] };
        }

        private static float PerpendicularDistance2D(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
        {
            float lx = lineEnd.x - lineStart.x, lz = lineEnd.z - lineStart.z;
            var lenSq = lx * lx + lz * lz;
            if (lenSq < 0.0001f) return VectorExtensions.DistXZ(point, lineStart);

            var t = ((point.x - lineStart.x) * lx + (point.z - lineStart.z) * lz) / lenSq;
            t = Math.Max(0f, Math.Min(1f, t));
            var projX = lineStart.x + t * lx;
            var projZ = lineStart.z + t * lz;
            float dx = point.x - projX, dz = point.z - projZ;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        #endregion

        #region Sharp Angle Pruning

        public static int PruneSharpAngles(List<Vector3> waypoints, float threshold)
        {
            if (waypoints.Count <= 4) return 0;
            var pruned = 0;
            for (var i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                var prev = (i - 1 + waypoints.Count) % waypoints.Count;
                var next = (i + 1) % waypoints.Count;
                var angle = InteriorAngleCW(waypoints[prev], waypoints[i], waypoints[next]);
                if (angle < threshold) continue;

                var directDist = VectorExtensions.DistXZ(waypoints[prev], waypoints[next]);
                if (directDist > 20f) continue;

                waypoints.RemoveAt(i);
                pruned++;
            }

            return pruned;
        }

        private static float InteriorAngleCW(Vector3 a, Vector3 b, Vector3 c)
        {
            float abX = b.x - a.x, abZ = b.z - a.z;
            float bcX = c.x - b.x, bcZ = c.z - b.z;
            var cross = abX * bcZ - abZ * bcX;
            var dot = abX * bcX + abZ * bcZ;
            var turnAngle = (float)Math.Atan2(cross, dot) * (180f / (float)Math.PI);
            var interior = 180f - turnAngle;
            if (interior < 0f) interior += 360f;
            if (interior >= 360f) interior -= 360f;
            return interior;
        }

        #endregion
    }
}