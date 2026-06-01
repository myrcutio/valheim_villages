using System.Collections.Generic;
using Xunit;
using Barrier = ValheimVillages.Algorithms.Barrier;
using ValheimVillages.Algorithms;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Region-discovery pass, barrier primitive: <see cref="FloodFill.CrossesBarrier" />
///     decides whether a single cell→cell step is severed by a door/wall segment.
///     It is the geometric gate the flood consults on every edge, so its
///     half-width and Y-tolerance handling are worth pinning independently of the
///     BFS that calls it.
/// </summary>
public class BarrierCrossingTests
{
    // Barrier on the plane x=0, blocking normal +x, perpendicular half-width 2
    // (HalfWidth2 = 4), Y-agnostic unless a tolerance is given.
    private static List<Barrier> WallAtOrigin(float yTolerance = 0f) => new()
    {
        new Barrier
        {
            Px = 0f, Pz = 0f, Py = 0f,
            Fx = 1f, Fz = 0f,
            HalfWidth2 = 4f,
            YTolerance = yTolerance,
        },
    };

    [Fact]
    public void SegmentCrossingWithinHalfWidth_IsBlocked()
    {
        // (-1,0) -> (1,0) crosses x=0 at z=0, dead center of the barrier.
        Assert.True(FloodFill.CrossesBarrier(-1f, 0f, 1f, 0f, 0f, WallAtOrigin()));
    }

    [Fact]
    public void SegmentEntirelyOnOneSide_IsNotBlocked()
    {
        // Both endpoints at x>0: never reaches the barrier plane.
        Assert.False(FloodFill.CrossesBarrier(1f, 0f, 2f, 0f, 0f, WallAtOrigin()));
    }

    [Fact]
    public void SegmentCrossingBeyondHalfWidth_IsNotBlocked()
    {
        // Crosses x=0 at z=5, which is 5m off-center — outside the 2m half-width.
        Assert.False(FloodFill.CrossesBarrier(-1f, 5f, 1f, 5f, 0f, WallAtOrigin()));
    }

    [Fact]
    public void YTolerance_IgnoresCrossingsOutsideTheBand()
    {
        var wall = WallAtOrigin(3f); // applies only within 3m of Py=0

        Assert.False(FloodFill.CrossesBarrier(-1f, 0f, 1f, 0f, 10f, wall),
            "cellY=10 is 10m above the barrier — outside the 3m band, so ignored");
        Assert.True(FloodFill.CrossesBarrier(-1f, 0f, 1f, 0f, 2f, wall),
            "cellY=2 is within the 3m band — the crossing still blocks");
    }
}
