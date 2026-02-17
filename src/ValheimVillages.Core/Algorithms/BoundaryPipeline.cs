using System;
using System.Collections.Generic;

namespace ValheimVillages.Algorithms
{
    /// <summary>
    /// Offline reimplementation of HnaBoundaryMapper's pure-geometry pipeline steps.
    /// NavMesh-dependent steps (re-snap, unpathable removal) are skipped; sharp angle
    /// pruning uses straight-line distance as a proxy for NavMesh pathability.
    /// </summary>
    public static class BoundaryPipeline
    {
        public class PipelineParams
        {
            public float RdpEpsilon { get; }
            public float SharpAngleThreshold { get; }
            public bool ChaikinEnabled { get; }
            public float XzDedupeRadius { get; }

            public PipelineParams(float rdpEpsilon, float sharpAngleThreshold,
                bool chaikinEnabled, float xzDedupeRadius)
            {
                RdpEpsilon = rdpEpsilon;
                SharpAngleThreshold = sharpAngleThreshold;
                ChaikinEnabled = chaikinEnabled;
                XzDedupeRadius = xzDedupeRadius;
            }
        }

        /// <summary>
        /// Run the full offline pipeline on edge-snapped positions, returning the final waypoints.
        /// </summary>
        public static List<Vec3> Run(List<Vec3> edgeSnapped, Vec3 bedCenter, PipelineParams p)
        {
            if (edgeSnapped.Count < 3) return new List<Vec3>(edgeSnapped);

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

        public static void SortClockwise(List<Vec3> waypoints, Vec3 center)
        {
            waypoints.Sort((a, b) =>
            {
                float angleA = (float)Math.Atan2(a.Z - center.Z, a.X - center.X);
                float angleB = (float)Math.Atan2(b.Z - center.Z, b.X - center.X);
                return angleB.CompareTo(angleA);
            });
        }

        #endregion

        #region Chaikin

        public static List<Vec3> ChaikinSmooth(List<Vec3> points)
        {
            if (points.Count < 3) return points;
            int n = points.Count;
            var result = new List<Vec3>(n * 2);
            for (int i = 0; i < n; i++)
            {
                int next = (i + 1) % n;
                var a = points[i];
                var b = points[next];
                result.Add(Vec3.Lerp(a, b, 0.25f));
                result.Add(Vec3.Lerp(a, b, 0.75f));
            }
            return result;
        }

        #endregion

        #region RDP

        public static List<Vec3> SimplifyRDP(List<Vec3> points, float epsilon)
        {
            if (points.Count <= 3) return points;

            int oppositeIdx = points.Count / 2;
            var firstHalf = points.GetRange(0, oppositeIdx + 1);
            var secondHalf = points.GetRange(oppositeIdx, points.Count - oppositeIdx);
            secondHalf.Add(points[0]);

            var simpFirst = RDPRecursive(firstHalf, epsilon);
            var simpSecond = RDPRecursive(secondHalf, epsilon);

            var result = new List<Vec3>(simpFirst);
            for (int i = 1; i < simpSecond.Count - 1; i++)
                result.Add(simpSecond[i]);

            return result.Count >= 3 ? result : points;
        }

        private static List<Vec3> RDPRecursive(List<Vec3> points, float epsilon)
        {
            if (points.Count <= 2) return new List<Vec3>(points);

            float maxDist = 0f;
            int maxIdx = 0;
            var start = points[0];
            var end = points[points.Count - 1];

            for (int i = 1; i < points.Count - 1; i++)
            {
                float dist = PerpendicularDistance2D(points[i], start, end);
                if (dist > maxDist) { maxDist = dist; maxIdx = i; }
            }

            if (maxDist > epsilon)
            {
                var left = points.GetRange(0, maxIdx + 1);
                var right = points.GetRange(maxIdx, points.Count - maxIdx);
                var simpLeft = RDPRecursive(left, epsilon);
                var simpRight = RDPRecursive(right, epsilon);
                var result = new List<Vec3>(simpLeft);
                for (int i = 1; i < simpRight.Count; i++)
                    result.Add(simpRight[i]);
                return result;
            }

            return new List<Vec3> { points[0], points[points.Count - 1] };
        }

        private static float PerpendicularDistance2D(Vec3 point, Vec3 lineStart, Vec3 lineEnd)
        {
            float lx = lineEnd.X - lineStart.X, lz = lineEnd.Z - lineStart.Z;
            float lenSq = lx * lx + lz * lz;
            if (lenSq < 0.0001f) return Vec3.DistXZ(point, lineStart);

            float t = ((point.X - lineStart.X) * lx + (point.Z - lineStart.Z) * lz) / lenSq;
            t = Math.Max(0f, Math.Min(1f, t));
            float projX = lineStart.X + t * lx;
            float projZ = lineStart.Z + t * lz;
            float dx = point.X - projX, dz = point.Z - projZ;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        #endregion

        #region Sharp Angle Pruning

        public static int PruneSharpAngles(List<Vec3> waypoints, float threshold)
        {
            if (waypoints.Count <= 4) return 0;
            int pruned = 0;
            for (int i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                int prev = (i - 1 + waypoints.Count) % waypoints.Count;
                int next = (i + 1) % waypoints.Count;
                float angle = InteriorAngleCW(waypoints[prev], waypoints[i], waypoints[next]);
                if (angle < threshold) continue;

                float directDist = Vec3.DistXZ(waypoints[prev], waypoints[next]);
                if (directDist > 20f) continue;

                waypoints.RemoveAt(i);
                pruned++;
            }
            return pruned;
        }

        private static float InteriorAngleCW(Vec3 a, Vec3 b, Vec3 c)
        {
            float abX = b.X - a.X, abZ = b.Z - a.Z;
            float bcX = c.X - b.X, bcZ = c.Z - b.Z;
            float cross = abX * bcZ - abZ * bcX;
            float dot = abX * bcX + abZ * bcZ;
            float turnAngle = (float)Math.Atan2(cross, dot) * (180f / (float)Math.PI);
            float interior = 180f - turnAngle;
            if (interior < 0f) interior += 360f;
            if (interior >= 360f) interior -= 360f;
            return interior;
        }

        #endregion

        #region Monotonic Angle

        public static List<Vec3> EnforceMonotonicAngle(List<Vec3> waypoints, Vec3 center)
        {
            if (waypoints.Count < 3) return waypoints;

            var result = new List<Vec3>(waypoints.Count) { waypoints[0] };
            float prevAngle = (float)Math.Atan2(waypoints[0].Z - center.Z, waypoints[0].X - center.X);

            for (int i = 1; i < waypoints.Count; i++)
            {
                float angle = (float)Math.Atan2(waypoints[i].Z - center.Z, waypoints[i].X - center.X);
                float delta = angle - prevAngle;

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

        public static List<Vec3> DeduplicateByXZ(List<Vec3> waypoints, float radius)
        {
            float radiusSq = radius * radius;
            var keep = new bool[waypoints.Count];
            for (int k = 0; k < keep.Length; k++) keep[k] = true;

            for (int i = 0; i < waypoints.Count; i++)
            {
                if (!keep[i]) continue;
                for (int j = i + 1; j < waypoints.Count; j++)
                {
                    if (!keep[j]) continue;
                    float dx = waypoints[i].X - waypoints[j].X;
                    float dz = waypoints[i].Z - waypoints[j].Z;
                    if (dx * dx + dz * dz > radiusSq) continue;

                    if (waypoints[j].Y > waypoints[i].Y)
                        keep[i] = false;
                    else
                        keep[j] = false;
                }
            }

            var result = new List<Vec3>();
            for (int i = 0; i < waypoints.Count; i++)
                if (keep[i]) result.Add(waypoints[i]);
            return result;
        }

        #endregion
    }
}
