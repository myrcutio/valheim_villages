using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Algorithms;
using Xunit;

namespace ValheimVillages.Tests.Patrol;

/// <summary>
///     Patrol boundary-mapping pass: <see cref="BoundaryPipeline" /> is the
///     offline, NavMesh-free reimplementation of the geometry steps that turn a
///     region's boundary cells into a patrol loop (sort → smooth → simplify →
///     prune). The deterministic primitives (Chaikin, XZ dedup, clockwise sort,
///     sharp-angle prune guard) are pinned exactly; the recursive/angular steps
///     (RDP, monotonic-angle, full Run) get contract-level property assertions
///     rather than brittle hand-computed coordinates.
/// </summary>
public class BoundaryPipelineTests
{
    private static Vector3 P(float x, float z) => new(x, 0f, z);

    // ----- ChaikinSmooth -----

    [Fact]
    public void Chaikin_FewerThanThreePoints_ReturnedUnchanged()
    {
        var pts = new List<Vector3> { P(0, 0), P(1, 1) };
        Assert.Same(pts, BoundaryPipeline.ChaikinSmooth(pts));
    }

    [Fact]
    public void Chaikin_DoublesPointCountAndStaysWithinHull()
    {
        var square = new List<Vector3> { P(-10, -10), P(10, -10), P(10, 10), P(-10, 10) };
        var smoothed = BoundaryPipeline.ChaikinSmooth(square);

        Assert.Equal(8, smoothed.Count);
        // Each cut point is a lerp between two corners, so it stays inside the
        // axis-aligned bounding box of the input.
        Assert.All(smoothed, p =>
        {
            Assert.InRange(p.x, -10f, 10f);
            Assert.InRange(p.z, -10f, 10f);
        });
    }

    // ----- DeduplicateByXZ -----

    [Fact]
    public void Dedup_CollapsesNearbyPoints_KeepingTheHigherY()
    {
        var pts = new List<Vector3> { new(0f, 0f, 0f), new(0.2f, 5f, 0.2f) };
        var result = BoundaryPipeline.DeduplicateByXZ(pts, 1f);

        var only = Assert.Single(result);
        Assert.Equal(5f, only.y);
    }

    [Fact]
    public void Dedup_KeepsPointsFartherApartThanRadius()
    {
        var pts = new List<Vector3> { P(0, 0), P(5, 0) };
        Assert.Equal(2, BoundaryPipeline.DeduplicateByXZ(pts, 1f).Count);
    }

    // ----- SortClockwise -----

    [Fact]
    public void SortClockwise_OrdersByDescendingAngleAroundCenter()
    {
        var east = P(1, 0);   // angle 0
        var north = P(0, 1);  // angle +pi/2
        var west = P(-1, 0);  // angle +pi
        var south = P(0, -1); // angle -pi/2
        var pts = new List<Vector3> { east, south, west, north };

        BoundaryPipeline.SortClockwise(pts, Vector3.zero);

        Assert.Equal(new[] { west, north, east, south }, pts);
    }

    // ----- PruneSharpAngles -----

    [Fact]
    public void PruneSharpAngles_FourOrFewerPoints_IsANoOp()
    {
        var pts = new List<Vector3> { P(0, 0), P(1, 0), P(1, 1), P(0, 1) };
        Assert.Equal(0, BoundaryPipeline.PruneSharpAngles(pts, 170f));
        Assert.Equal(4, pts.Count);
    }

    [Fact]
    public void PruneSharpAngles_RemovesRedundantCollinearPoint_DownToFour()
    {
        // Five near-collinear points: one interior point sits at ~180deg between
        // its neighbours with a short bypass, so it is pruned; the floor is 4.
        var pts = new List<Vector3> { P(0, 0), P(1, 0), P(2, 0), P(3, 0), P(4, 0) };
        var pruned = BoundaryPipeline.PruneSharpAngles(pts, 170f);

        Assert.Equal(1, pruned);
        Assert.Equal(4, pts.Count);
    }

    // ----- SimplifyRDP -----

    [Fact]
    public void SimplifyRDP_ThreeOrFewerPoints_ReturnedUnchanged()
    {
        var pts = new List<Vector3> { P(0, 0), P(1, 0), P(2, 0) };
        Assert.Same(pts, BoundaryPipeline.SimplifyRDP(pts, 0.5f));
    }

    [Fact]
    public void SimplifyRDP_CollinearRun_IsReduced()
    {
        var pts = new List<Vector3> { P(0, 0), P(1, 0), P(2, 0), P(3, 0), P(4, 0), P(5, 0) };
        var simplified = BoundaryPipeline.SimplifyRDP(pts, 0.1f);

        Assert.True(simplified.Count < pts.Count, "collinear interior points should drop out");
        Assert.True(simplified.Count >= 2);
    }

    // ----- EnforceMonotonicAngle -----

    [Fact]
    public void EnforceMonotonicAngle_FewerThanThree_ReturnedUnchanged()
    {
        var pts = new List<Vector3> { P(1, 0), P(0, 1) };
        Assert.Same(pts, BoundaryPipeline.EnforceMonotonicAngle(pts, Vector3.zero));
    }

    [Fact]
    public void EnforceMonotonicAngle_AlreadyClockwise_KeepsAllPoints()
    {
        // Strictly descending angle around the center (pi, pi/2, 0, -pi/2): every
        // step is a forward clockwise move, so nothing is dropped.
        var pts = new List<Vector3> { P(-1, 0), P(0, 1), P(1, 0), P(0, -1) };
        var result = BoundaryPipeline.EnforceMonotonicAngle(pts, Vector3.zero);
        Assert.Equal(4, result.Count);
    }

    // ----- Run (integration smoke) -----

    [Fact]
    public void Run_NoisyLoop_ProducesAFiniteWaypointLoop()
    {
        var pts = new List<Vector3>
        {
            P(10, 0), P(7, 7), P(0, 10), P(-7, 7),
            P(-10, 0), P(-7, -7), P(0, -10), P(7, -7),
        };
        var p = new BoundaryPipeline.PipelineParams(
            rdpEpsilon: 0.5f, sharpAngleThreshold: 170f, chaikinEnabled: true, xzDedupeRadius: 0.5f);

        var result = BoundaryPipeline.Run(pts, Vector3.zero, p);

        Assert.True(result.Count >= 3, "a usable patrol loop needs at least 3 waypoints");
        Assert.All(result, wp =>
        {
            Assert.False(float.IsNaN(wp.x) || float.IsNaN(wp.z), "no NaN waypoints");
        });
    }
}
