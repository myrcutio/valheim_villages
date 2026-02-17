using System;
using System.Collections.Generic;

namespace ValheimVillages.Algorithms
{
    /// <summary>
    /// Scores a pipeline-generated waypoint path against a player-walked reference path.
    /// All distances measured in XZ (horizontal plane) unless stated otherwise.
    /// </summary>
    public static class PathScoring
    {
        public class ScoreResult
        {
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

            public ScoreResult(float hausdorff, float meanDistance, float coverage, int waypointCount)
            {
                Hausdorff = hausdorff;
                MeanDistance = meanDistance;
                Coverage = coverage;
                WaypointCount = waypointCount;
            }
        }

        /// <summary>
        /// Score the pipeline output against the reference perimeter path.
        /// </summary>
        public static ScoreResult Score(
            List<Vec3> pipeline, List<Vec3> reference, float coverageRadius = 3f)
        {
            if (pipeline.Count == 0 || reference.Count == 0)
                return new ScoreResult(float.MaxValue, float.MaxValue, 0f, pipeline.Count);

            float maxRefToPipe = 0f;
            float sumRefToPipe = 0f;
            int coveredCount = 0;

            foreach (var rp in reference)
            {
                float minDist = NearestDistXZ(rp, pipeline);
                if (minDist > maxRefToPipe) maxRefToPipe = minDist;
                sumRefToPipe += minDist;
                if (minDist <= coverageRadius) coveredCount++;
            }

            float maxPipeToRef = 0f;
            foreach (var pp in pipeline)
            {
                float minDist = NearestDistXZ(pp, reference);
                if (minDist > maxPipeToRef) maxPipeToRef = minDist;
            }

            float hausdorff = Math.Max(maxRefToPipe, maxPipeToRef);
            float meanDist = sumRefToPipe / reference.Count;
            float coverage = (float)coveredCount / reference.Count;

            return new ScoreResult(hausdorff, meanDist, coverage, pipeline.Count);
        }

        /// <summary>
        /// Score the pipeline output against the reference using segment-based distances.
        /// </summary>
        public static ScoreResult ScoreSegments(
            List<Vec3> pipeline, List<Vec3> reference, float coverageRadius = 3f)
        {
            if (pipeline.Count < 2 || reference.Count == 0)
                return Score(pipeline, reference, coverageRadius);

            float maxRefToPipe = 0f;
            float sumRefToPipe = 0f;
            int coveredCount = 0;

            foreach (var rp in reference)
            {
                float minDist = NearestSegmentDistXZ(rp, pipeline);
                if (minDist > maxRefToPipe) maxRefToPipe = minDist;
                sumRefToPipe += minDist;
                if (minDist <= coverageRadius) coveredCount++;
            }

            float maxPipeToRef = 0f;
            foreach (var pp in pipeline)
            {
                float minDist = NearestDistXZ(pp, reference);
                if (minDist > maxPipeToRef) maxPipeToRef = minDist;
            }

            float hausdorff = Math.Max(maxRefToPipe, maxPipeToRef);
            float meanDist = sumRefToPipe / reference.Count;
            float coverage = (float)coveredCount / reference.Count;

            return new ScoreResult(hausdorff, meanDist, coverage, pipeline.Count);
        }

        private static float NearestDistXZ(Vec3 point, List<Vec3> candidates)
        {
            float best = float.MaxValue;
            foreach (var c in candidates)
            {
                float d = Vec3.DistXZ(point, c);
                if (d < best) best = d;
            }
            return best;
        }

        private static float NearestSegmentDistXZ(Vec3 point, List<Vec3> polygon)
        {
            float best = float.MaxValue;
            for (int i = 0; i < polygon.Count; i++)
            {
                int next = (i + 1) % polygon.Count;
                float d = PointToSegmentDistXZ(point, polygon[i], polygon[next]);
                if (d < best) best = d;
            }
            return best;
        }

        private static float PointToSegmentDistXZ(Vec3 p, Vec3 a, Vec3 b)
        {
            float abx = b.X - a.X, abz = b.Z - a.Z;
            float lenSq = abx * abx + abz * abz;
            if (lenSq < 0.0001f) return Vec3.DistXZ(p, a);

            float t = ((p.X - a.X) * abx + (p.Z - a.Z) * abz) / lenSq;
            t = Math.Max(0f, Math.Min(1f, t));
            float projX = a.X + t * abx;
            float projZ = a.Z + t * abz;
            float dx = p.X - projX, dz = p.Z - projZ;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
