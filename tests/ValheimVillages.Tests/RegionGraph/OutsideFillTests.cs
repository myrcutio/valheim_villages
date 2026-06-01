using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Pass 1 (outside-in fill) support: the pure pieces that surround the
///     engine-bound perimeter flood. The flood itself (<c>Physics.OverlapBox</c>
///     wall gating + <c>ZoneSystem</c> heightmap) is deferred to engine-level
///     tests, but its <b>output representation</b> is pure and testable here:
///     the packed XZ-key coordinate space the outside-cell set lives in
///     (<see cref="RubberBandPrune.PackXzKey" /> / <c>UnpackOutsideCellKey</c>),
///     the point-membership query (<see cref="RubberBandPrune.IsOutsideCell" />),
///     and the greedy-rectangle decomposition that collapses the cell set into
///     ModifierBox volumes for the second bake
///     (<see cref="RubberBandPrune.DecomposeToRectangles" />).
/// </summary>
public class OutsideFillTests
{
    // ----- XZ key codec -----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 7)]
    [InlineData(-3, -9)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(123456, -654321)]
    public void PackUnpack_RoundTrips(int gx, int gz)
    {
        var key = RubberBandPrune.PackXzKey(gx, gz);
        RubberBandPrune.UnpackOutsideCellKey(key, out var ux, out var uz);
        Assert.Equal(gx, ux);
        Assert.Equal(gz, uz);
    }

    [Fact]
    public void PackXzKey_DistinctCellsDoNotCollide()
    {
        var seen = new HashSet<long>();
        for (var gx = -10; gx <= 10; gx++)
        for (var gz = -10; gz <= 10; gz++)
            Assert.True(seen.Add(RubberBandPrune.PackXzKey(gx, gz)),
                $"key collision at ({gx},{gz})");
    }

    // ----- IsOutsideCell (LookupCellSize == 1) -----

    [Fact]
    public void IsOutsideCell_PointInsideAMarkedCell_IsTrue()
    {
        var outside = new HashSet<long> { RubberBandPrune.PackXzKey(0, 0) };
        Assert.True(RubberBandPrune.IsOutsideCell(new Vector3(0.5f, 12f, 0.5f), outside));
    }

    [Fact]
    public void IsOutsideCell_PointInAnUnmarkedCell_IsFalse()
    {
        var outside = new HashSet<long> { RubberBandPrune.PackXzKey(0, 0) };
        Assert.False(RubberBandPrune.IsOutsideCell(new Vector3(1.5f, 12f, 0.5f), outside));
    }

    [Fact]
    public void IsOutsideCell_NegativeCoordinatesFloorCorrectly()
    {
        var outside = new HashSet<long> { RubberBandPrune.PackXzKey(-1, -1) };
        Assert.True(RubberBandPrune.IsOutsideCell(new Vector3(-0.5f, 0f, -0.5f), outside));
    }

    [Fact]
    public void IsOutsideCell_EmptyOrNullSet_IsFalse()
    {
        Assert.False(RubberBandPrune.IsOutsideCell(new Vector3(0.5f, 0f, 0.5f), new HashSet<long>()));
        Assert.False(RubberBandPrune.IsOutsideCell(new Vector3(0.5f, 0f, 0.5f), null));
    }

    // ----- DecomposeToRectangles -----

    private static HashSet<long> Cells(params (int gx, int gz)[] cells)
    {
        var set = new HashSet<long>();
        foreach (var (gx, gz) in cells)
            set.Add(RubberBandPrune.PackXzKey(gx, gz));
        return set;
    }

    private static HashSet<long> Expand(List<RubberBandPrune.CellRect> rects)
    {
        var set = new HashSet<long>();
        var overlaps = 0;
        foreach (var r in rects)
        for (var x = r.Gx0; x <= r.Gx1; x++)
        for (var z = r.Gz0; z <= r.Gz1; z++)
            if (!set.Add(RubberBandPrune.PackXzKey(x, z)))
                overlaps++;
        Assert.Equal(0, overlaps); // rectangles must be disjoint
        return set;
    }

    [Fact]
    public void Decompose_EmptySet_ProducesNoRectangles()
    {
        Assert.Empty(RubberBandPrune.DecomposeToRectangles(new HashSet<long>()));
    }

    [Fact]
    public void Decompose_SingleCell_ProducesOneUnitRect()
    {
        var rects = RubberBandPrune.DecomposeToRectangles(Cells((3, 4)));
        var r = Assert.Single(rects);
        Assert.Equal((3, 4, 3, 4), (r.Gx0, r.Gz0, r.Gx1, r.Gz1));
    }

    [Fact]
    public void Decompose_FilledRectangle_CollapsesToOneRect()
    {
        // 3 wide (x 0..2) x 2 tall (z 0..1), fully populated.
        var cells = Cells(
            (0, 0), (1, 0), (2, 0),
            (0, 1), (1, 1), (2, 1));
        var rects = RubberBandPrune.DecomposeToRectangles(cells);

        var r = Assert.Single(rects);
        Assert.Equal((0, 0, 2, 1), (r.Gx0, r.Gz0, r.Gx1, r.Gz1));
    }

    [Fact]
    public void Decompose_NegativeCoordinateRectangle_CollapsesToOneRect()
    {
        var cells = Cells((-2, -3), (-1, -3), (-2, -2), (-1, -2));
        var r = Assert.Single(RubberBandPrune.DecomposeToRectangles(cells));
        Assert.Equal((-2, -3, -1, -2), (r.Gx0, r.Gz0, r.Gx1, r.Gz1));
    }

    [Fact]
    public void Decompose_TwoDisjointCells_ProduceTwoRects()
    {
        var rects = RubberBandPrune.DecomposeToRectangles(Cells((0, 0), (10, 10)));
        Assert.Equal(2, rects.Count);
    }

    [Fact]
    public void Decompose_NonRectangularBlob_ExactlyCoversTheInput()
    {
        // L-shape: full 3x3 minus the (2,2) corner. The exact rectangle count is
        // an implementation detail; the contract is "the rectangles tile the
        // input set exactly, with no gaps, no spillover, and no overlap".
        var cells = Cells(
            (0, 0), (1, 0), (2, 0),
            (0, 1), (1, 1), (2, 1),
            (0, 2), (1, 2));
        var rects = RubberBandPrune.DecomposeToRectangles(cells);

        Assert.True(Expand(rects).SetEquals(cells),
            "decomposed rectangles must tile the input cells exactly");
    }
}
