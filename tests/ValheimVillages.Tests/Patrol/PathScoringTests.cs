using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Algorithms;
using Xunit;

namespace ValheimVillages.Tests.Patrol;

/// <summary>
///     Patrol boundary-mapping pass, scoring: <see cref="PathScoring" /> grades a
///     generated patrol loop against a reference perimeter (Hausdorff, mean
///     distance, coverage). Pure XZ geometry — used to characterize boundary
///     quality without driving a villager around the map.
/// </summary>
public class PathScoringTests
{
    private static Vector3 P(float x, float z) => new(x, 0f, z);

    [Fact]
    public void IdenticalPaths_ScorePerfectly()
    {
        var path = new List<Vector3> { P(0, 0), P(10, 0), P(10, 10) };
        var r = PathScoring.Score(path, path);

        Assert.Equal(0f, r.Hausdorff);
        Assert.Equal(0f, r.MeanDistance);
        Assert.Equal(1f, r.Coverage);
        Assert.Equal(3, r.WaypointCount);
    }

    [Fact]
    public void EmptyPipeline_ScoresWorst()
    {
        var r = PathScoring.Score(new List<Vector3>(), new List<Vector3> { P(0, 0) });
        Assert.Equal(float.MaxValue, r.Hausdorff);
        Assert.Equal(float.MaxValue, r.MeanDistance);
        Assert.Equal(0f, r.Coverage);
    }

    [Fact]
    public void EmptyReference_ScoresWorst()
    {
        var r = PathScoring.Score(new List<Vector3> { P(0, 0) }, new List<Vector3>());
        Assert.Equal(float.MaxValue, r.Hausdorff);
        Assert.Equal(0f, r.Coverage);
    }

    [Fact]
    public void UniformOffset_WithinCoverageRadius_IsFullyCovered()
    {
        var pipeline = new List<Vector3> { P(0, 0) };
        var reference = new List<Vector3> { P(3, 0) };
        var r = PathScoring.Score(pipeline, reference, coverageRadius: 3f);

        Assert.InRange(r.MeanDistance, 2.99f, 3.01f);
        Assert.InRange(r.Hausdorff, 2.99f, 3.01f);
        Assert.Equal(1f, r.Coverage);
    }

    [Fact]
    public void PartialCoverage_CountsOnlyReferencePointsWithinRadius()
    {
        var pipeline = new List<Vector3> { P(0, 0) };
        var reference = new List<Vector3> { P(1, 0), P(10, 0) };
        var r = PathScoring.Score(pipeline, reference, coverageRadius: 3f);

        Assert.Equal(0.5f, r.Coverage);                 // 1 of 2 within 3m
        Assert.InRange(r.MeanDistance, 5.49f, 5.51f);   // (1 + 10) / 2
        Assert.InRange(r.Hausdorff, 9.99f, 10.01f);     // worst-case 10m
    }

    [Fact]
    public void ScoreSegments_MeasuresDistanceToEdges_NotJustVertices()
    {
        // The reference point sits 2m off the midpoint of the pipeline segment
        // but ~5.4m from either endpoint. Segment scoring should report ~2.
        var pipeline = new List<Vector3> { P(0, 0), P(10, 0) };
        var reference = new List<Vector3> { P(5, 2) };

        var vertexScore = PathScoring.Score(pipeline, reference);
        var segmentScore = PathScoring.ScoreSegments(pipeline, reference);

        Assert.InRange(segmentScore.MeanDistance, 1.99f, 2.01f);
        Assert.True(segmentScore.MeanDistance < vertexScore.MeanDistance,
            "segment distance should be tighter than nearest-vertex distance");
    }
}
