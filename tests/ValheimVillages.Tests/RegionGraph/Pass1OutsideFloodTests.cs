using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Pass 1 (outside-in perimeter flood), now pure via injected providers:
///     <see cref="RubberBandPrune.PerimeterOutsideFlood" />. Determines which
///     cells lie outside the outermost wall ring by flooding inward from the
///     bake-bounds perimeter, severing steps at walls. These tests pin the
///     reachability edge cases that used to require a live bake to observe.
/// </summary>
public class Pass1OutsideFloodTests
{
    private static HashSet<long> Flood(
        GridEnv env, int gxMin, int gzMin, int gxMax, int gzMax, out int seeds) =>
        RubberBandPrune.PerimeterOutsideFlood(
            gxMin, gzMin, gxMax, gzMax, env.CellY, env.WallBlocks, out seeds);

    [Fact]
    public void OpenGrid_EveryCellIsOutside()
    {
        // 7x7 with no walls: the flood reaches all 49 cells.
        var outside = Flood(new GridEnv(), 0, 0, 6, 6, out var seeds);

        Assert.Equal(49, outside.Count);
        Assert.Equal(24, seeds); // 2*7 + 2*5 border cells
    }

    [Fact]
    public void SealedBlock_InteriorIsNotOutside()
    {
        // A 5x5 block (cells 2..6) fully walled off from the surrounding ground
        // on a 9x9 grid. None of its 25 cells can be reached from the perimeter.
        var env = new GridEnv().SealBlock(2, 2, 6, 6);
        var outside = Flood(env, 0, 0, 8, 8, out _);

        Assert.Equal(81 - 25, outside.Count);
        Assert.Contains(GridEnv.Key(0, 0), outside);          // open ground outside
        Assert.DoesNotContain(GridEnv.Key(2, 2), outside);    // block corner
        Assert.DoesNotContain(GridEnv.Key(4, 4), outside);    // block center
        Assert.DoesNotContain(GridEnv.Key(6, 6), outside);    // block far corner
    }

    [Fact]
    public void LayeredWalls_FloodStopsAtOuterRing_InnerCourtyardNotOutside()
    {
        // Concentric seals on an 11x11 grid: an outer 7x7 block (cells 2..8) and
        // an inner 3x3 courtyard (cells 4..6). The flood must stop at the OUTER
        // ring — neither the between-ring band nor the inner courtyard may be
        // marked "outside". This is the regression the production comment calls
        // out: layered walls must not let secondary courtyards read as outside.
        var env = new GridEnv()
            .SealBlock(2, 2, 8, 8)
            .SealBlock(4, 4, 6, 6);
        var outside = Flood(env, 0, 0, 10, 10, out _);

        Assert.Equal(121 - 49, outside.Count);                // only the rim outside the 7x7
        Assert.Contains(GridEnv.Key(0, 0), outside);          // outside the outer ring
        Assert.DoesNotContain(GridEnv.Key(3, 3), outside);    // between the two rings
        Assert.DoesNotContain(GridEnv.Key(5, 5), outside);    // inner courtyard center
    }

    [Fact]
    public void GapInWall_LetsTheFloodLeakInside()
    {
        // The same sealed 5x5, but one boundary edge is re-opened. The flood
        // pours through the gap and every interior cell becomes outside again.
        var env = new GridEnv()
            .SealBlock(2, 2, 6, 6)
            .Open(4, 2, 4, 1); // re-open the south edge of cell (4,2)
        var outside = Flood(env, 0, 0, 8, 8, out _);

        Assert.Equal(81, outside.Count);
        Assert.Contains(GridEnv.Key(4, 4), outside);
    }

    [Fact]
    public void WallGate_HonorsTheHeightArguments()
    {
        // The helper must pass each cell's Y to the wall gate. With a gate that
        // blocks whenever neighbouring heights differ by >0.5:
        //   - a flat grid never blocks  → all cells outside;
        //   - a grid where every step is a 1m change blocks every edge → only
        //     the perimeter seeds are "outside", the interior is unreachable.
        Func<int, int, int, int, float, float, bool> stepGate =
            (_, _, _, _, ya, yb) => Math.Abs(ya - yb) > 0.5f;

        var flat = RubberBandPrune.PerimeterOutsideFlood(
            0, 0, 4, 4, (_, _) => 0f, stepGate, out _);
        Assert.Equal(25, flat.Count);

        var stepped = RubberBandPrune.PerimeterOutsideFlood(
            0, 0, 4, 4, (gx, gz) => gx + gz, stepGate, out var seeds);
        Assert.Equal(seeds, stepped.Count);                   // no expansion past seeds
        Assert.DoesNotContain(GridEnv.Key(2, 2), stepped);    // interior never reached
    }
}
