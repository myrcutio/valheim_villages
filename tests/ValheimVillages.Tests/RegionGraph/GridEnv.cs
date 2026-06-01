using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Synthetic, engine-free environment for the <see cref="RubberBandPrune" />
///     flood passes. Replaces the live Physics/ZoneSystem providers with
///     hand-authored data: walls are blocked cell-pairs, heights are an explicit
///     map, and "populated" is an explicit set. Lets the pure Pass-1/Pass-2
///     floods be driven entirely from fixtures — no game, no bake, no save.
/// </summary>
internal sealed class GridEnv
{
    private readonly HashSet<(int, int, int, int)> _walls = new();
    private readonly Dictionary<long, float> _heights = new();
    private readonly HashSet<long> _populated = new();

    /// <summary>Sever the (undirected) step between two adjacent cells.</summary>
    public GridEnv Wall(int ax, int az, int bx, int bz)
    {
        _walls.Add(Canon(ax, az, bx, bz));
        return this;
    }

    /// <summary>Re-open a previously severed step (for "gap in the wall" cases).</summary>
    public GridEnv Open(int ax, int az, int bx, int bz)
    {
        _walls.Remove(Canon(ax, az, bx, bz));
        return this;
    }

    /// <summary>Give a cell a region surface height (also marks it populated).</summary>
    public GridEnv Height(int gx, int gz, float y)
    {
        _heights[Key(gx, gz)] = y;
        _populated.Add(Key(gx, gz));
        return this;
    }

    /// <summary>Mark a cell as covered by a region without setting an explicit height.</summary>
    public GridEnv Populate(int gx, int gz)
    {
        _populated.Add(Key(gx, gz));
        return this;
    }

    /// <summary>Seal a rectangular block by blocking every edge crossing its border.</summary>
    public GridEnv SealBlock(int x0, int z0, int x1, int z1)
    {
        for (var x = x0; x <= x1; x++)
        {
            Wall(x, z0, x, z0 - 1);
            Wall(x, z1, x, z1 + 1);
        }

        for (var z = z0; z <= z1; z++)
        {
            Wall(x0, z, x0 - 1, z);
            Wall(x1, z, x1 + 1, z);
        }

        return this;
    }

    public Func<int, int, float> CellY =>
        (gx, gz) => _heights.TryGetValue(Key(gx, gz), out var y) ? y : 0f;

    public Func<long, int, int, float> SurfaceY =>
        (k, _, _) => _heights.TryGetValue(k, out var y) ? y : 0f;

    public Func<long, bool> IsPopulated => k => _populated.Contains(k);

    // Wall gate: blocks a step iff the (unordered) cell pair was walled. The Y
    // args are ignored here so flood-topology tests stay deterministic; the
    // Y-dependent gate is exercised separately with a custom delegate.
    public Func<int, int, int, int, float, float, bool> WallBlocks =>
        (ax, az, bx, bz, _, _) => _walls.Contains(Canon(ax, az, bx, bz));

    public static long Key(int gx, int gz) => RubberBandPrune.PackXzKey(gx, gz);

    /// <summary>All border cells of an inclusive grid rectangle, as packed keys.</summary>
    public static HashSet<long> Border(int gxMin, int gzMin, int gxMax, int gzMax)
    {
        var s = new HashSet<long>();
        for (var x = gxMin; x <= gxMax; x++)
        {
            s.Add(Key(x, gzMin));
            s.Add(Key(x, gzMax));
        }

        for (var z = gzMin; z <= gzMax; z++)
        {
            s.Add(Key(gxMin, z));
            s.Add(Key(gxMax, z));
        }

        return s;
    }

    private static (int, int, int, int) Canon(int ax, int az, int bx, int bz) =>
        ax < bx || (ax == bx && az <= bz) ? (ax, az, bx, bz) : (bx, bz, ax, az);
}
