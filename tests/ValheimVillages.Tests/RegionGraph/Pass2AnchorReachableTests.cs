using System.Linq;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Pass 2 (anchor inside-out flood), now pure via injected providers:
///     <see cref="RubberBandPrune.AnchorReachableFlood" />. Floods the set of cells a
///     villager can actually reach from a anchor — snapping the anchor onto the floor
///     it rests on, refusing to walk into "outside" cells, and stopping at walls.
///     Driven here with synthetic populated/height/wall fixtures (cell size 1).
/// </summary>
public class Pass2AnchorReachableTests
{
    private static void Flood(
        GridEnv env, Vector3[] anchors, HashSet<long> outside,
        out HashSet<long> reach, out Dictionary<long, List<float>> reachY, out int seeds,
        System.Action<string>? warn = null) =>
        RubberBandPrune.AnchorReachableFlood(
            0, 0, 6, 6, anchors, outside, cell: 1f, anchorSnapRingMax: 6,
            env.IsPopulated, env.SurfacesAt, env.WallBlocks, maxClimb: 1f,
            out reach, out reachY, out seeds, out _, out _, out _, warn);

    private static Vector3[] AnchorAtCell(int gx, int gz) =>
        new[] { new Vector3(gx + 0.5f, 0f, gz + 0.5f) };

    [Fact]
    public void NoAnchors_ProducesNothing()
    {
        Flood(new GridEnv(), System.Array.Empty<Vector3>(), new HashSet<long>(),
            out var reach, out _, out var seeds);

        Assert.Empty(reach);
        Assert.Equal(0, seeds);
    }

    [Fact]
    public void Anchor_FloodsTheInterior_StoppingAtOutsideCells()
    {
        // Interior 5x5 reachable; the perimeter ring is "outside" and walls it in.
        var env = new GridEnv().Populate(3, 3);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, AnchorAtCell(3, 3), outside, out var reach, out _, out var seeds);

        Assert.Equal(1, seeds);
        Assert.Equal(25, reach.Count);
        Assert.Contains(GridEnv.Key(1, 1), reach);
        Assert.Contains(GridEnv.Key(5, 5), reach);
        Assert.DoesNotContain(GridEnv.Key(0, 0), reach);
    }

    [Fact]
    public void Anchor_SnapsToNearestPopulatedCell_WhenItsOwnCellIsUnusable()
    {
        // Only (3,4) is populated and non-outside; the anchor sits on (3,3), which
        // is "outside" (e.g. its floor cell was carved). The snap walks outward
        // and seeds (3,4) instead of stranding the flood on the carved cell.
        var env = new GridEnv().Populate(3, 4);
        var outside = new HashSet<long>();
        for (var gx = 0; gx <= 6; gx++)
        for (var gz = 0; gz <= 6; gz++)
            if (!(gx == 3 && gz == 4))
                outside.Add(GridEnv.Key(gx, gz));

        Flood(env, AnchorAtCell(3, 3), outside, out var reach, out _, out var seeds);

        Assert.Equal(1, seeds);
        var only = Assert.Single(reach);
        Assert.Equal(GridEnv.Key(3, 4), only);
    }

    [Fact]
    public void Anchor_WalledOff_IsSkippedWithAWarning()
    {
        // Nothing is populated within reach, so no snap target exists: the anchor is
        // skipped and the caller is warned rather than silently dropped.
        var warnings = new List<string>();
        Flood(new GridEnv(), AnchorAtCell(3, 3), new HashSet<long>(),
            out var reach, out _, out var seeds, warnings.Add);

        Assert.Equal(0, seeds);
        Assert.Empty(reach);
        var msg = Assert.Single(warnings);
        Assert.Contains("anchor skipped", msg);
    }

    [Fact]
    public void Flood_RefusesToEnterOutsideCells()
    {
        // An interior cell flagged "outside" is excluded even though the flood
        // surrounds it on all sides.
        var env = new GridEnv().Populate(3, 3);
        var outside = GridEnv.Border(0, 0, 6, 6);
        outside.Add(GridEnv.Key(3, 4));

        Flood(env, AnchorAtCell(3, 3), outside, out var reach, out _, out _);

        Assert.Equal(24, reach.Count);
        Assert.DoesNotContain(GridEnv.Key(3, 4), reach);
        Assert.Contains(GridEnv.Key(3, 5), reach); // reachable around the hole
    }

    [Fact]
    public void Wall_SeversAnInternalRoom()
    {
        // A full-height wall between columns 3 and 4 splits the interior; a anchor on
        // the left reaches only the 15 left-hand cells, not the locked-off right.
        var env = new GridEnv().Populate(2, 3);
        for (var gz = 1; gz <= 5; gz++)
            env.Wall(3, gz, 4, gz);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, AnchorAtCell(2, 3), outside, out var reach, out _, out _);

        Assert.Equal(15, reach.Count);
        Assert.Contains(GridEnv.Key(2, 3), reach);
        Assert.Contains(GridEnv.Key(3, 5), reach);
        Assert.DoesNotContain(GridEnv.Key(4, 3), reach);
        Assert.DoesNotContain(GridEnv.Key(5, 3), reach);
    }

    [Fact]
    public void Flood_RecordsTheWalkSurfaceHeightPerCell()
    {
        // anchorReachableCellY must carry each reached cell's surface Y so Pass 3 can
        // seed the piece flood at the right altitude.
        var env = new GridEnv().Height(3, 3, 12.5f).Height(3, 4, 13.0f);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, AnchorAtCell(3, 3), outside, out _, out var reachY, out _);

        Assert.Equal(12.5f, Assert.Single(reachY[GridEnv.Key(3, 3)]));
        Assert.Equal(13.0f, Assert.Single(reachY[GridEnv.Key(3, 4)]));
    }

    /// <summary>
    ///     A column can hold more than one walkable surface — a floor piece resting over open
    ///     ground — and both are places a villager stands. Collapsing a column to its HIGHEST
    ///     surface is what confined an upstairs-seeded flood to the building's own footprint:
    ///     the ground floor under the upper storey was never walkable, so the ground-level
    ///     doorway (the only way out) was never even evaluated. Measured in game: 131 reachable
    ///     cells and 14 committed regions, against 1300+ cells and 70+ regions once the flood
    ///     can walk down.
    /// </summary>
    [Fact]
    public void Flood_CarriesEverySurfaceInAStackedColumn()
    {
        var env = new GridEnv()
            .Height(3, 3, 0f).Surface(3, 3, 0.8f)
            .Height(3, 4, 0f).Surface(3, 4, 0.8f);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, AnchorAtCell(3, 3), outside, out var reach, out var reachY, out _);

        Assert.Contains(GridEnv.Key(3, 4), reach);
        Assert.Equal(new[] { 0f, 0.8f }, reachY[GridEnv.Key(3, 3)].OrderBy(y => y).ToArray());
        Assert.Equal(new[] { 0f, 0.8f }, reachY[GridEnv.Key(3, 4)].OrderBy(y => y).ToArray());
    }

    /// <summary>
    ///     The guard on the above. A stacked column is climb-gated, so the flood must not hop
    ///     from open ground onto a roof 4m up merely because they share a cell. A column with a
    ///     SINGLE surface keeps its old ungated behaviour, so steep terrain is unaffected.
    /// </summary>
    [Fact]
    public void Flood_DoesNotHopOntoAStackedSurfaceBeyondClimb()
    {
        var env = new GridEnv()
            .Height(3, 3, 0f)
            .Height(3, 4, 0f).Surface(3, 4, 4f);
        var outside = GridEnv.Border(0, 0, 6, 6);

        Flood(env, AnchorAtCell(3, 3), outside, out _, out var reachY, out _);

        Assert.Equal(0f, Assert.Single(reachY[GridEnv.Key(3, 4)]));
    }
}
