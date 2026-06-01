using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Pass 2 (bed inside-out flood), now pure via injected providers:
///     <see cref="RubberBandPrune.BedReachableFlood" />. Floods the set of cells a
///     villager can actually reach from a bed — snapping the bed onto the floor
///     it rests on, refusing to walk into "outside" cells, and stopping at walls.
///     Driven here with synthetic populated/height/wall fixtures (cell size 1).
/// </summary>
public class Pass2BedReachableTests
{
    private static void Flood(
        GridEnv env, Vector3[] beds, HashSet<long> outside,
        out HashSet<long> reach, out Dictionary<long, float> reachY, out int seeds,
        System.Action<string>? warn = null) =>
        RubberBandPrune.BedReachableFlood(
            0, 0, 6, 6, beds, outside, cell: 1f, bedSnapRingMax: 6,
            env.IsPopulated, env.SurfaceY, env.WallBlocks,
            out reach, out reachY, out seeds, warn);

    private static Vector3[] BedAtCell(int gx, int gz) =>
        new[] { new Vector3(gx + 0.5f, 0f, gz + 0.5f) };

    [Fact]
    public void NoBeds_ProducesNothing()
    {
        Flood(new GridEnv(), System.Array.Empty<Vector3>(), new HashSet<long>(),
            out var reach, out _, out var seeds);

        Assert.Empty(reach);
        Assert.Equal(0, seeds);
    }

    [Fact]
    public void Bed_FloodsTheInterior_StoppingAtOutsideCells()
    {
        // Interior 5x5 reachable; the perimeter ring is "outside" and walls it in.
        var env = new GridEnv().Populate(3, 3);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, BedAtCell(3, 3), outside, out var reach, out _, out var seeds);

        Assert.Equal(1, seeds);
        Assert.Equal(25, reach.Count);
        Assert.Contains(GridEnv.Key(1, 1), reach);
        Assert.Contains(GridEnv.Key(5, 5), reach);
        Assert.DoesNotContain(GridEnv.Key(0, 0), reach);
    }

    [Fact]
    public void Bed_SnapsToNearestPopulatedCell_WhenItsOwnCellIsUnusable()
    {
        // Only (3,4) is populated and non-outside; the bed sits on (3,3), which
        // is "outside" (e.g. its floor cell was carved). The snap walks outward
        // and seeds (3,4) instead of stranding the flood on the carved cell.
        var env = new GridEnv().Populate(3, 4);
        var outside = new HashSet<long>();
        for (var gx = 0; gx <= 6; gx++)
        for (var gz = 0; gz <= 6; gz++)
            if (!(gx == 3 && gz == 4))
                outside.Add(GridEnv.Key(gx, gz));

        Flood(env, BedAtCell(3, 3), outside, out var reach, out _, out var seeds);

        Assert.Equal(1, seeds);
        var only = Assert.Single(reach);
        Assert.Equal(GridEnv.Key(3, 4), only);
    }

    [Fact]
    public void Bed_WalledOff_IsSkippedWithAWarning()
    {
        // Nothing is populated within reach, so no snap target exists: the bed is
        // skipped and the caller is warned rather than silently dropped.
        var warnings = new List<string>();
        Flood(new GridEnv(), BedAtCell(3, 3), new HashSet<long>(),
            out var reach, out _, out var seeds, warnings.Add);

        Assert.Equal(0, seeds);
        Assert.Empty(reach);
        var msg = Assert.Single(warnings);
        Assert.Contains("bed skipped", msg);
    }

    [Fact]
    public void Flood_RefusesToEnterOutsideCells()
    {
        // An interior cell flagged "outside" is excluded even though the flood
        // surrounds it on all sides.
        var env = new GridEnv().Populate(3, 3);
        var outside = GridEnv.Border(0, 0, 6, 6);
        outside.Add(GridEnv.Key(3, 4));

        Flood(env, BedAtCell(3, 3), outside, out var reach, out _, out _);

        Assert.Equal(24, reach.Count);
        Assert.DoesNotContain(GridEnv.Key(3, 4), reach);
        Assert.Contains(GridEnv.Key(3, 5), reach); // reachable around the hole
    }

    [Fact]
    public void Wall_SeversAnInternalRoom()
    {
        // A full-height wall between columns 3 and 4 splits the interior; a bed on
        // the left reaches only the 15 left-hand cells, not the locked-off right.
        var env = new GridEnv().Populate(2, 3);
        for (var gz = 1; gz <= 5; gz++)
            env.Wall(3, gz, 4, gz);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, BedAtCell(2, 3), outside, out var reach, out _, out _);

        Assert.Equal(15, reach.Count);
        Assert.Contains(GridEnv.Key(2, 3), reach);
        Assert.Contains(GridEnv.Key(3, 5), reach);
        Assert.DoesNotContain(GridEnv.Key(4, 3), reach);
        Assert.DoesNotContain(GridEnv.Key(5, 3), reach);
    }

    [Fact]
    public void Flood_RecordsTheWalkSurfaceHeightPerCell()
    {
        // bedReachableCellY must carry each reached cell's surface Y so Pass 3 can
        // seed the piece flood at the right altitude.
        var env = new GridEnv().Height(3, 3, 12.5f).Height(3, 4, 13.0f);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, BedAtCell(3, 3), outside, out _, out var reachY, out _);

        Assert.Equal(12.5f, reachY[GridEnv.Key(3, 3)]);
        Assert.Equal(13.0f, reachY[GridEnv.Key(3, 4)]);
    }
}
