using System;
using System.Collections.Generic;
using ValheimVillages.Algorithms;
using Xunit;
using Barrier = ValheimVillages.Algorithms.Barrier;

namespace ValheimVillages.Tests.Algorithms;

/// <summary>
/// Tests for the FloodFill BFS algorithm: barrier handling, region isolation.
/// </summary>
public class FloodFillTests
{
    [Fact]
    public void Run_SingleBed_FloodsFlatTerrain()
    {
        var beds = new[] { new Vec3(0, 0, 0) };
        var barriers = new List<Barrier>();

        // Flat terrain: always return Y=0
        HeightLookup heightLookup = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = 0f;
            return true;
        };

        var result = FloodFill.Run(
            beds, barriers, heightLookup,
            originX: -30f, originZ: -30f,
            cellSize: 1f, cellCountX: 60, cellCountZ: 60);

        Assert.True(result.Count > 0, "Should flood at least some cells");
        // With 25m radius on flat terrain, expect a large region
        Assert.True(result.Count > 100,
            $"Expected >100 cells on flat terrain, got {result.Count}");
    }

    [Fact]
    public void Run_NoBeds_ReturnsEmpty()
    {
        var beds = Array.Empty<Vec3>();
        var barriers = new List<Barrier>();
        HeightLookup heightLookup = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = 0f;
            return true;
        };

        var result = FloodFill.Run(beds, barriers, heightLookup,
            0, 0, 1f, 10, 10);

        Assert.Empty(result);
    }

    [Fact]
    public void Run_RadiusLimitsCellSpread()
    {
        var beds = new[] { new Vec3(15, 0, 15) };
        var barriers = new List<Barrier>();
        HeightLookup heightLookup = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = 0f;
            return true;
        };

        var result = FloodFill.Run(beds, barriers, heightLookup,
            0, 0, 1f, 60, 60);

        // All cells should be within FloodFillRadius (25m) of the bed
        float r2 = FloodFill.FloodFillRadius * FloodFill.FloodFillRadius;
        foreach (var cell in result)
        {
            float dx = cell.Wx - 15f, dz = cell.Wz - 15f;
            float dist2 = dx * dx + dz * dz;
            Assert.True(dist2 <= r2 * 1.5f, // allow some cell-size margin
                $"Cell ({cell.Wx:F1},{cell.Wz:F1}) is too far from bed: {Math.Sqrt(dist2):F1}m > {FloodFill.FloodFillRadius}m");
        }
    }

    [Fact]
    public void Run_SteepSlope_BlocksExpansion()
    {
        var beds = new[] { new Vec3(5, 0, 5) };
        var barriers = new List<Barrier>();

        // Height jumps dramatically at x=8 (simulates cliff)
        HeightLookup heightLookup = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = wx > 8 ? 20f : 0f; // 20m cliff
            return true;
        };

        var result = FloodFill.Run(beds, barriers, heightLookup,
            0, 0, 1f, 20, 20);

        // Cells beyond x=8 should be blocked by slope
        var beyondCliff = result.FindAll(c => c.Wx > 9f);
        Assert.Empty(beyondCliff);
    }

    [Fact]
    public void CrossesBarrier_DetectsIntersection()
    {
        var barriers = new List<Barrier>
        {
            new Barrier
            {
                Px = 5, Pz = 5, Py = 0,
                Fx = 1, Fz = 0,
                HalfWidth2 = 4f,
                YTolerance = 0
            }
        };

        // Line crossing the barrier
        Assert.True(FloodFill.CrossesBarrier(3, 5, 7, 5, 0, barriers));
        // Line not crossing
        Assert.False(FloodFill.CrossesBarrier(0, 0, 3, 0, 0, barriers));
    }

    [Fact]
    public void CrossesBarrier_YTolerance_IgnoresFarFloors()
    {
        var barriers = new List<Barrier>
        {
            new Barrier
            {
                Px = 5, Pz = 5, Py = 0,
                Fx = 1, Fz = 0,
                HalfWidth2 = 4f,
                YTolerance = 3f // only blocks within 3m of Y=0
            }
        };

        // Same floor: should block
        Assert.True(FloodFill.CrossesBarrier(3, 5, 7, 5, 0, barriers));
        // Different floor (Y=10): should not block
        Assert.False(FloodFill.CrossesBarrier(3, 5, 7, 5, 10, barriers));
    }
}
