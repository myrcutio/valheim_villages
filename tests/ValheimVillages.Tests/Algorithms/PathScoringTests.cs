using UnityEngine;
using ValheimVillages.Algorithms;
using Xunit;

namespace ValheimVillages.Tests.Algorithms;

/// <summary>
///     Tests for PathScoring: point-based and segment-based scoring.
/// </summary>
public class PathScoringTests
{
    [Fact]
    public void Score_IdenticalPaths_PerfectScore()
    {
        var path = MakeCircle(10f, 20);
        var result = PathScoring.Score(path, path);

        Assert.Equal(0f, result.Hausdorff);
        Assert.Equal(0f, result.MeanDistance);
        Assert.Equal(1f, result.Coverage);
    }

    [Fact]
    public void Score_EmptyPipeline_ReturnsMaxValues()
    {
        var reference = MakeCircle(10f, 20);
        var result = PathScoring.Score(new List<Vector3>(), reference);

        Assert.Equal(float.MaxValue, result.Hausdorff);
        Assert.Equal(0f, result.Coverage);
    }

    [Fact]
    public void Score_NearbyPaths_HighCoverage()
    {
        var pipeline = MakeCircle(10f, 20);
        // Reference is a slightly offset version
        var reference = MakeCircle(10.5f, 20);

        var result = PathScoring.Score(pipeline, reference, 3f);

        Assert.True(result.Coverage > 0.9f,
            $"Coverage should be >90% for nearby paths, got {result.Coverage:P0}");
        Assert.True(result.MeanDistance < 2f,
            $"Mean distance should be small, got {result.MeanDistance:F2}");
    }

    [Fact]
    public void Score_FarPaths_LowCoverage()
    {
        var pipeline = MakeCircle(10f, 20);
        var reference = MakeCircle(50f, 20); // Very different radius

        var result = PathScoring.Score(pipeline, reference, 3f);

        Assert.True(result.Coverage < 0.5f,
            $"Coverage should be low for far paths, got {result.Coverage:P0}");
    }

    [Fact]
    public void ScoreSegments_BetterThanPointScore_ForSparsePolygon()
    {
        // Sparse pipeline (4 points, square) vs dense reference (circle)
        var pipeline = new List<Vector3>
        {
            new(10, 0, 10),
            new(-10, 0, 10),
            new(-10, 0, -10),
            new(10, 0, -10),
        };
        var reference = MakeCircle(10f, 100);

        var pointScore = PathScoring.Score(pipeline, reference, 5f);
        var segmentScore = PathScoring.ScoreSegments(pipeline, reference, 5f);

        // Segment scoring should give equal or better coverage
        Assert.True(segmentScore.Coverage >= pointScore.Coverage,
            $"Segment coverage ({segmentScore.Coverage:P0}) should be >= point coverage ({pointScore.Coverage:P0})");
    }

    [Fact]
    public void Combined_LowerIsBetter()
    {
        var path = MakeCircle(10f, 20);

        var perfect = PathScoring.Score(path, path);
        var bad = PathScoring.Score(path, MakeCircle(50f, 20));

        Assert.True(perfect.Combined < bad.Combined,
            $"Perfect combined ({perfect.Combined:F2}) should be less than bad ({bad.Combined:F2})");
    }

    #region Helpers

    private static List<Vector3> MakeCircle(float radius, int points)
    {
        var result = new List<Vector3>(points);
        for (var i = 0; i < points; i++)
        {
            var angle = 2f * MathF.PI * i / points;
            result.Add(new Vector3(
                radius * MathF.Cos(angle), 0,
                radius * MathF.Sin(angle)));
        }

        return result;
    }

    #endregion
}