using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using RegionGraphType = ValheimVillages.Villager.AI.Navigation.RegionGraph;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Synthetic, engine-free village for <see cref="RubberBandPrune.PieceReachableFlood" />
///     (Pass 3). Where <see cref="GridEnv" /> models the flat cell grid the Pass-1/Pass-2
///     floods walk, this models the layered world Pass 3 walks: piece regions stacked at
///     arbitrary heights over terrain cells, indexed exactly the way
///     <c>RubberBandPrune.Apply</c> indexes them.
///
///     <para>Everything is hand-authored, so a test says what geometry it means — "a floor at
///     y=0, a stair region at y=1.05, an upper floor at y=2.0" — instead of depending on a
///     captured save, a bake, or a running game. The previous attempt at headless coverage
///     (tests/HnaPartitionTests, deleted) failed because it REIMPLEMENTED the algorithms and
///     drifted; this drives the production code through its injected oracle.</para>
/// </summary>
internal sealed class PieceGridEnv
{
    private readonly Dictionary<long, List<long>> _piece = new();
    private readonly Dictionary<long, List<long>> _terrain = new();
    private readonly Dictionary<long, string> _lookup = new();
    private readonly Dictionary<string, Vector3> _centroids = new();
    private readonly HashSet<long> _anchorReachable = new();
    private readonly Dictionary<long, float> _anchorReachableY = new();
    private readonly HashSet<long> _outside = new();
    private readonly Dictionary<long, float> _keySurfaceY = new();

    /// <summary>Cell size. 1m matches <c>RegionGraph.LookupCellSize</c> in production.</summary>
    public float Cell { get; init; } = 1f;

    /// <summary>
    ///     Ground height for cells Pass 2 recorded nothing for. Constant by default — tests
    ///     that care about terrain relief set their own.
    /// </summary>
    public Func<float, float, float>? GroundY { get; init; } = (_, _) => 0f;

    /// <summary>
    ///     A terrain cell Pass 2 reached, standing at <paramref name="y" />. Optionally give
    ///     it a terrain REGION too, which is what a piece step uses as its `fromRid` when it
    ///     steps off open ground.
    /// </summary>
    public PieceGridEnv Ground(int gx, int gz, float y = 0f, string? regionId = null)
    {
        var xz = RubberBandPrune.PackXzKey(gx, gz);
        _anchorReachable.Add(xz);
        _anchorReachableY[xz] = y;

        if (regionId == null) return this;

        var lookupKey = RegionGraphType.PackLookup(gx, gz, RegionGraphType.HeightBucket(y));
        Index(_terrain, xz, lookupKey);
        _lookup[lookupKey] = regionId;
        _centroids.TryAdd(regionId, new Vector3(gx * Cell + Cell * 0.5f, y, gz * Cell + Cell * 0.5f));
        return this;
    }

    /// <summary>
    ///     A piece region occupying one cell at height <paramref name="y" />. Several calls
    ///     with the same <paramref name="regionId" /> build a multi-cell region; the centroid
    ///     is taken from the FIRST call, because Pass 3 gates on the region centroid and a
    ///     test that means "this region's centroid is at 1.05" should say so once.
    /// </summary>
    public PieceGridEnv Piece(int gx, int gz, float y, string regionId)
    {
        var xz = RubberBandPrune.PackXzKey(gx, gz);
        var lookupKey = RegionGraphType.PackLookup(gx, gz, RegionGraphType.HeightBucket(y));
        Index(_piece, xz, lookupKey);
        _lookup[lookupKey] = regionId;
        // The cell's TRUE surface, the way production derives it from the triangles.
        if (!_keySurfaceY.TryGetValue(lookupKey, out var known) || y > known)
            _keySurfaceY[lookupKey] = y;
        _centroids.TryAdd(regionId, new Vector3(gx * Cell + Cell * 0.5f, y, gz * Cell + Cell * 0.5f));
        return this;
    }

    /// <summary>
    ///     Override a region's centroid — the AVERAGE of its cells, which for a merged stair
    ///     flight or a ramp sits well away from any particular tread. Pass 3 must judge a step
    ///     by the cell it is stepping onto, never by this.
    /// </summary>
    public PieceGridEnv Centroid(string regionId, float x, float y, float z)
    {
        _centroids[regionId] = new Vector3(x, y, z);
        return this;
    }

    /// <summary>
    ///     A flight rising from <paramref name="yBottom" /> to <paramref name="yTop" /> across
    ///     cells gx0..gx1, merged into ONE region whose centroid is their mean — the shape
    ///     RegionBuilder's coplanar merge actually produces for a staircase.
    /// </summary>
    public PieceGridEnv MergedFlight(int gx0, int gx1, int gz, float yBottom, float yTop, string regionId)
    {
        var steps = gx1 - gx0;
        for (var i = 0; i <= steps; i++)
        {
            var y = steps == 0 ? yBottom : yBottom + (yTop - yBottom) * i / steps;
            Piece(gx0 + i, gz, y, regionId);
        }

        return Centroid(regionId,
            (gx0 + gx1) * 0.5f * Cell + Cell * 0.5f,
            (yBottom + yTop) * 0.5f,
            gz * Cell + Cell * 0.5f);
    }

    /// <summary>A run of piece cells along +X at one height — a corridor, or a landing.</summary>
    public PieceGridEnv PieceRun(int gx0, int gx1, int gz, float y, string regionId)
    {
        for (var gx = gx0; gx <= gx1; gx++) Piece(gx, gz, y, regionId);
        return this;
    }

    /// <summary>Mark a cell as beyond the village perimeter (Pass 1's verdict).</summary>
    public PieceGridEnv Outside(int gx, int gz)
    {
        _outside.Add(RubberBandPrune.PackXzKey(gx, gz));
        return this;
    }

    /// <summary>Run Pass 3 over this fixture.</summary>
    public Pass3Result Run(float maxClimb = 1.0f)
    {
        RubberBandPrune.PieceReachableFlood(
            _piece, _terrain, _lookup, _centroids, _keySurfaceY,
            _anchorReachable, _anchorReachableY, _outside,
            Cell, maxClimb, GroundY,
            out var reachableKeys, out var edges, out var added);
        return new Pass3Result(reachableKeys, edges, added, _lookup);
    }

    private static void Index(Dictionary<long, List<long>> map, long xz, long lookupKey)
    {
        if (!map.TryGetValue(xz, out var list))
        {
            list = new List<long>();
            map[xz] = list;
        }

        if (!list.Contains(lookupKey)) list.Add(lookupKey);
    }
}

/// <summary>Pass-3 output, with the lookup grid kept so assertions can talk in region ids.</summary>
internal sealed record Pass3Result(
    HashSet<long> ReachableKeys,
    List<(string fromRid, string toRid, Vector3 startPos, Vector3 endPos)> Edges,
    int PieceKeysAdded,
    Dictionary<long, string> Lookup)
{
    /// <summary>Region ids the flood actually claimed at least one cell of.</summary>
    public HashSet<string> ReachedRegions()
    {
        var ids = new HashSet<string>();
        foreach (var key in ReachableKeys)
            if (Lookup.TryGetValue(key, out var rid))
                ids.Add(rid);
        return ids;
    }

    public bool Reached(string regionId) => ReachedRegions().Contains(regionId);

    public bool HasEdge(string a, string b) =>
        Edges.Any(e => (e.fromRid == a && e.toRid == b) || (e.fromRid == b && e.toRid == a));
}
