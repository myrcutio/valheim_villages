using System;
using System.Collections.Generic;
using ValheimVillages.Algorithms;
using Xunit;

namespace ValheimVillages.Tests.Algorithms;

/// <summary>
/// Tests for BoundaryPipeline geometry operations:
/// Chaikin smoothing, RDP simplification, clockwise sort, XZ dedup.
/// </summary>
public class BoundaryPipelineTests
{
    private static readonly Vec3 Origin = new Vec3(0, 0, 0);

    [Fact]
    public void ChaikinSmooth_DoublesPointCount()
    {
        var points = MakeSquare(10f);
        var smoothed = BoundaryPipeline.ChaikinSmooth(points);

        Assert.Equal(points.Count * 2, smoothed.Count);
    }

    [Fact]
    public void ChaikinSmooth_TooFewPoints_ReturnsUnchanged()
    {
        var points = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 1) };
        var result = BoundaryPipeline.ChaikinSmooth(points);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void SimplifyRDP_ReducesPointCount()
    {
        // Generate a circle with many points
        var points = MakeCircle(10f, 100);
        var simplified = BoundaryPipeline.SimplifyRDP(points, 0.5f);

        Assert.True(simplified.Count < points.Count,
            $"RDP should reduce {points.Count} points, got {simplified.Count}");
        Assert.True(simplified.Count >= 3, "Must keep at least 3 points");
    }

    [Fact]
    public void SimplifyRDP_SmallEpsilon_KeepsMorePoints()
    {
        var points = MakeCircle(10f, 50);
        var tight = BoundaryPipeline.SimplifyRDP(points, 0.1f);
        var loose = BoundaryPipeline.SimplifyRDP(points, 2.0f);

        Assert.True(tight.Count >= loose.Count,
            $"Smaller epsilon should keep more points: tight={tight.Count}, loose={loose.Count}");
    }

    [Fact]
    public void SortClockwise_ProducesDescendingAngles()
    {
        var points = new List<Vec3>
        {
            new Vec3(1, 0, 0),   // 0°
            new Vec3(0, 0, 1),   // 90°
            new Vec3(-1, 0, 0),  // 180°
            new Vec3(0, 0, -1)   // 270°/-90°
        };

        BoundaryPipeline.SortClockwise(points, Origin);

        // After clockwise sort, angles should be descending
        for (int i = 0; i < points.Count - 1; i++)
        {
            float a1 = MathF.Atan2(points[i].Z, points[i].X);
            float a2 = MathF.Atan2(points[i + 1].Z, points[i + 1].X);
            Assert.True(a1 >= a2, $"Angle at {i} ({a1:F2}) should be >= angle at {i + 1} ({a2:F2})");
        }
    }

    [Fact]
    public void DeduplicateByXZ_RemovesDuplicatesKeepingHigherY()
    {
        var points = new List<Vec3>
        {
            new Vec3(0, 5, 0),
            new Vec3(0.5f, 10, 0.5f),  // within radius 1.0 of first, higher Y
            new Vec3(10, 0, 10)
        };

        var result = BoundaryPipeline.DeduplicateByXZ(points, 1.0f);

        Assert.Equal(2, result.Count);
        // The higher Y point should be kept
        Assert.Contains(result, p => p.Y == 10);
        Assert.Contains(result, p => p.X == 10);
    }

    [Fact]
    public void PruneSharpAngles_RemovesSharpTurns()
    {
        // Create a polygon with one very sharp angle
        var points = new List<Vec3>
        {
            new Vec3(0, 0, 0),
            new Vec3(5, 0, 0),
            new Vec3(5.1f, 0, 0.1f),  // Very sharp turn
            new Vec3(10, 0, 0),
            new Vec3(10, 0, 10),
            new Vec3(0, 0, 10)
        };

        int pruned = BoundaryPipeline.PruneSharpAngles(points, 170f);
        Assert.True(pruned > 0, "Should have pruned at least one sharp angle");
    }

    [Fact]
    public void FullPipeline_ProducesValidOutput()
    {
        var edgeSnapped = MakeCircle(20f, 50);
        var bedCenter = Origin;
        var p = new BoundaryPipeline.PipelineParams(1.5f, 160f, true, 1.0f);

        var result = BoundaryPipeline.Run(edgeSnapped, bedCenter, p);

        Assert.NotNull(result);
        Assert.True(result.Count >= 3, $"Pipeline should produce ≥3 waypoints, got {result.Count}");
        Assert.True(result.Count <= edgeSnapped.Count,
            $"Pipeline should simplify {edgeSnapped.Count} to ≤{edgeSnapped.Count} points");
    }

    [Fact]
    public void FullPipeline_TooFewInputPoints_ReturnsInput()
    {
        var edgeSnapped = new List<Vec3> { new Vec3(0, 0, 0), new Vec3(1, 0, 0) };
        var p = new BoundaryPipeline.PipelineParams(1.5f, 160f, true, 1.0f);

        var result = BoundaryPipeline.Run(edgeSnapped, Origin, p);

        Assert.Equal(edgeSnapped.Count, result.Count);
    }

    #region Helpers

    private static List<Vec3> MakeSquare(float size)
    {
        return new List<Vec3>
        {
            new Vec3(size, 0, size),
            new Vec3(-size, 0, size),
            new Vec3(-size, 0, -size),
            new Vec3(size, 0, -size)
        };
    }

    private static List<Vec3> MakeCircle(float radius, int points)
    {
        var result = new List<Vec3>(points);
        for (int i = 0; i < points; i++)
        {
            float angle = 2f * MathF.PI * i / points;
            result.Add(new Vec3(
                radius * MathF.Cos(angle), 0,
                radius * MathF.Sin(angle)));
        }
        return result;
    }

    #endregion
}
