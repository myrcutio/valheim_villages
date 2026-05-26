using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Algorithms
{
    /// <summary>
    ///     Scores a pipeline-generated waypoint path against a player-walked reference path.
    ///     All distances measured in XZ (horizontal plane) unless stated otherwise.
    /// </summary>
    public static class PathScoring
    {
        /// <summary>
        ///     Score the pipeline output against the reference perimeter path.
        /// </summary>
        public static ScoreResult Score(
            List<Vector3> pipeline, List<Vector3> reference, float coverageRadius = 3f)
        {
            if (pipeline.Count == 0 || reference.Count == 0)
                return new ScoreResult(float.MaxValue, float.MaxValue, 0f, pipeline.Count);

            var maxRefToPipe = 0f;
            var sumRefToPipe = 0f;
            var coveredCount = 0;

            foreach (var rp in reference)
            {
                var minDist = NearestDistXZ(rp, pipeline);
                if (minDist > maxRefToPipe) maxRefToPipe = minDist;
                sumRefToPipe += minDist;
                if (minDist <= coverageRadius) coveredCount++;
            }

            var maxPipeToRef = 0f;
            foreach (var pp in pipeline)
            {
                var minDist = NearestDistXZ(pp, reference);
                if (minDist > maxPipeToRef) maxPipeToRef = minDist;
            }

            var hausdorff = Math.Max(maxRefToPipe, maxPipeToRef);
            var meanDist = sumRefToPipe / reference.Count;
            var coverage = (float)coveredCount / reference.Count;

            return new ScoreResult(hausdorff, meanDist, coverage, pipeline.Count);
        }

        /// <summary>
        ///     Score the pipeline output against the reference using segment-based distances.
        /// </summary>
        public static ScoreResult ScoreSegments(
            List<Vector3> pipeline, List<Vector3> reference, float coverageRadius = 3f)
        {
            if (pipeline.Count < 2 || reference.Count == 0)
                return Score(pipeline, reference, coverageRadius);

            var maxRefToPipe = 0f;
            var sumRefToPipe = 0f;
            var coveredCount = 0;

            foreach (var rp in reference)
            {
                var minDist = NearestSegmentDistXZ(rp, pipeline);
                if (minDist > maxRefToPipe) maxRefToPipe = minDist;
                sumRefToPipe += minDist;
                if (minDist <= coverageRadius) coveredCount++;
            }

            var maxPipeToRef = 0f;
            foreach (var pp in pipeline)
            {
                var minDist = NearestDistXZ(pp, reference);
                if (minDist > maxPipeToRef) maxPipeToRef = minDist;
            }

            var hausdorff = Math.Max(maxRefToPipe, maxPipeToRef);
            var meanDist = sumRefToPipe / reference.Count;
            var coverage = (float)coveredCount / reference.Count;

            return new ScoreResult(hausdorff, meanDist, coverage, pipeline.Count);
        }

        private static float NearestDistXZ(Vector3 point, List<Vector3> candidates)
        {
            var best = float.MaxValue;
            foreach (var c in candidates)
            {
                var d = VectorExtensions.DistXZ(point, c);
                if (d < best) best = d;
            }

            return best;
        }

        private static float NearestSegmentDistXZ(Vector3 point, List<Vector3> polygon)
        {
            var best = float.MaxValue;
            for (var i = 0; i < polygon.Count; i++)
            {
                var next = (i + 1) % polygon.Count;
                var d = PointToSegmentDistXZ(point, polygon[i], polygon[next]);
                if (d < best) best = d;
            }

            return best;
        }

        private static float PointToSegmentDistXZ(Vector3 p, Vector3 a, Vector3 b)
        {
            float abx = b.x - a.x, abz = b.z - a.z;
            var lenSq = abx * abx + abz * abz;
            if (lenSq < 0.0001f) return VectorExtensions.DistXZ(p, a);

            var t = ((p.x - a.x) * abx + (p.z - a.z) * abz) / lenSq;
            t = Math.Max(0f, Math.Min(1f, t));
            var projX = a.x + t * abx;
            var projZ = a.z + t * abz;
            float dx = p.x - projX, dz = p.z - projZ;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public class ScoreResult
        {
            public ScoreResult(float hausdorff, float meanDistance, float coverage, int waypointCount)
            {
                Hausdorff = hausdorff;
                MeanDistance = meanDistance;
                Coverage = coverage;
                WaypointCount = waypointCount;
            }

            public float Hausdorff { get; }
            public float MeanDistance { get; }
            public float Coverage { get; }
            public int WaypointCount { get; }

            /// <summary>Combined score: lower is better. Weights tuned for patrol quality.</summary>
            public float Combined =>
                MeanDistance * 2f +
                Hausdorff * 0.5f +
                (1f - Coverage) * 20f +
                WaypointCount * 0.05f;
        }
    }
}