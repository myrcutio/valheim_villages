using System;
using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Algorithms;
using Xunit;
using Barrier = ValheimVillages.Algorithms.Barrier;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Region-discovery pass: the BFS flood that grows a walkable region outward
///     from bed seeds (<see cref="FloodFill" />). This is the pure, engine-free
///     core — the live pipeline injects a <see cref="HeightLookup" /> backed by
///     Physics raycasts and a barrier list built from door/wall pieces; here we
///     drive it with synthetic height functions and hand-authored barriers so
///     every reachability edge case is deterministic and save-independent.
/// </summary>
public class FloodFillTests
{
    // Standard test grid: 60x60 cells of size 1, origin (-30,-30), so cell
    // (ix,iz) has its center at world (-30 + ix + 0.5, -30 + iz + 0.5). A bed
    // at the world origin therefore seeds cell (30,30).
    private const float Origin = -30f;
    private const float Cell = 1f;
    private const int Count = 60;

    private static HeightLookup Flat(float y = 0f) =>
        (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = y;
            return true;
        };

    private static List<RegionCell> RunStd(Vector3[] beds, HeightLookup h, List<Barrier>? barriers = null) =>
        FloodFill.Run(beds, barriers ?? new List<Barrier>(), h, Origin, Origin, Cell, Count, Count);

    private static bool Has(List<RegionCell> cells, int ix, int iz)
    {
        foreach (var c in cells)
            if (c.Ix == ix && c.Iz == iz)
                return true;
        return false;
    }

    [Fact]
    public void NoBeds_ReturnsEmpty()
    {
        Assert.Empty(RunStd(Array.Empty<Vector3>(), Flat()));
    }

    [Fact]
    public void BedOutsideGrid_ContributesNothing()
    {
        // ix = (1000 - (-30)) / 1 = 1030, far past the 60-cell grid → skipped.
        Assert.Empty(RunStd(new[] { new Vector3(1000f, 0f, 0f) }, Flat()));
    }

    [Fact]
    public void SingleBed_FlatTerrain_FloodsRadiusDiscAroundSeed()
    {
        var result = RunStd(new[] { new Vector3(0f, 0f, 0f) }, Flat());

        Assert.True(Has(result, 30, 30), "bed seed cell present");
        Assert.True(Has(result, 40, 30), "cell 10m away (well inside 25m radius) present");
        Assert.False(Has(result, 58, 30), "cell ~28m away (inside grid, beyond 25m radius) excluded");

        // Every discovered cell must lie within the flood radius of the bed
        // (+ a half-cell slack for cell-center geometry).
        foreach (var c in result)
        {
            var d = Mathf.Sqrt(c.Wx * c.Wx + c.Wz * c.Wz);
            Assert.True(d <= FloodFill.FloodFillRadius + 0.75f,
                $"cell ({c.Ix},{c.Iz}) at {d:F2}m exceeds flood radius");
        }
    }

    [Fact]
    public void VerticalCliff_StopsFloodAtTheStep()
    {
        // Flat at y=0 for x<5, a sheer 100m wall beyond — the slope between a
        // sub-5 cell and a past-5 cell is ~89deg, well over MaxWalkableSlopeDeg.
        HeightLookup cliff = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = wx < 5f ? 0f : 100f;
            return true;
        };
        var result = RunStd(new[] { new Vector3(0f, 0f, 0f) }, cliff);

        Assert.True(Has(result, 20, 30), "cell at x=-9.5 (flat, in radius) present");
        Assert.True(Has(result, 34, 30), "cell at x=4.5 (just below the cliff) present");
        Assert.False(Has(result, 35, 30), "cell at x=5.5 (top of the cliff) excluded by slope");
        Assert.False(Has(result, 40, 30), "cell at x=10.5 (past the cliff, in radius) excluded");
    }

    [Fact]
    public void GentleSlope_WithinThreshold_FloodsThrough()
    {
        // 0.4 rise per 1m step => atan(0.4) ~= 21.8deg < 26deg threshold, so the
        // flood climbs the ramp instead of stopping at it.
        HeightLookup ramp = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = wx * 0.4f;
            return true;
        };
        var result = RunStd(new[] { new Vector3(0f, 0f, 0f) }, ramp);

        Assert.True(Has(result, 40, 30), "cell 10m up a 21.8deg ramp is still reachable");
    }

    [Fact]
    public void HeightLookupFailure_CarvesAnImpassableStrip()
    {
        // A vertical strip x in [4,6) where the height provider reports "no
        // ground" (e.g. a void / unbaked gap). The flood may not enter those
        // cells and so cannot cross to the far side either.
        HeightLookup withHole = (float wx, float wz, float refY, out float hitY) =>
        {
            hitY = 0f;
            return !(wx >= 4f && wx < 6f);
        };
        var result = RunStd(new[] { new Vector3(0f, 0f, 0f) }, withHole);

        Assert.True(Has(result, 33, 30), "cell at x=3.5 (near side) present");
        Assert.False(Has(result, 35, 30), "cell at x=5.5 (inside the no-ground strip) excluded");
        Assert.False(Has(result, 40, 30), "cell at x=10.5 (far side, sealed off by the strip) excluded");
    }

    [Fact]
    public void Barrier_BlocksFloodAcrossIt()
    {
        // A wall barrier on the plane x=2.0, normal along +x, effectively
        // infinite width, Y-agnostic. The flood cannot step from x<2 to x>2.
        var wall = new List<Barrier>
        {
            new Barrier
            {
                Px = 2.0f, Pz = 0f, Py = 0f,
                Fx = 1f, Fz = 0f,
                HalfWidth2 = 10000f,
                YTolerance = 0f,
            },
        };
        var result = RunStd(new[] { new Vector3(0f, 0f, 0f) }, Flat(), wall);

        Assert.True(Has(result, 31, 30), "cell at x=1.5 (near side of the wall) present");
        Assert.False(Has(result, 32, 30), "cell at x=2.5 (far side) excluded");
        Assert.False(Has(result, 34, 30), "cell at x=4.5 (far side) excluded");
    }

    [Fact]
    public void MultipleBeds_FloodIndependentlyAndLeaveAGap()
    {
        // Two beds 60m apart on a 120-cell grid. Each floods its own 25m disc;
        // the 10m gap between the discs stays unreachable from either seed.
        var beds = new[] { new Vector3(-30f, 0f, 0f), new Vector3(30f, 0f, 0f) };
        var result = FloodFill.Run(beds, new List<Barrier>(), Flat(), -60f, -60f, 1f, 120, 120);

        Assert.True(Has(result, 30, 60), "bed #1 seed (x=-29.5) present");
        Assert.True(Has(result, 90, 60), "bed #2 seed (x=29.5) present");
        Assert.False(Has(result, 60, 60), "midpoint (x=0.5) is >25m from both beds — excluded");
    }
}
