using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Pass 3 decides which piece cells a villager can walk onto, and it is the pass that has
///     twice silently deleted parts of a working village. Every test here is a REAL failure,
///     measured in game, written down so it costs 80ms instead of a rebuild, a restart and an
///     afternoon of probing.
/// </summary>
public class Pass3PieceReachableTests
{
    /// <summary>The value production ships. See RubberBandPrune.PieceReachableFlood.</summary>
    private const float ShippedClimb = 1.0f;

    /// <summary>What the bake uses for its own step height (NavMeshBakeMaxClimb).</summary>
    private const float BakeClimb = 0.2f;

    // -------------------------------------------------------------------------
    // The MaxClimb trade-off — pinned from BOTH sides.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     A stair flight is ONE region whose centroid sits about a metre above the floor it
    ///     leaves, so the shipped 1.0m climb must carry the flood up it and onto the storey
    ///     above.
    /// </summary>
    [Fact]
    public void StairFlight_IsReached_AtTheShippedClimb()
    {
        var result = Building().Run(ShippedClimb);

        Assert.True(result.Reached("stair"), "the stair flight must stay reachable");
        Assert.True(result.Reached("upper"), "the storey above the stairs must stay reachable");
        Assert.True(result.HasEdge("floor", "stair"), "floor should link to the stairs");
        Assert.True(result.HasEdge("stair", "upper"), "stairs should link to the storey above");
    }

    /// <summary>
    ///     And the converse, which is the part that actually cost something. MaxClimb was once
    ///     changed 1.0 -> 0.2 on the reasoning that it should match the bake's step height. It
    ///     should not: this chains between region CENTROIDS, not between adjacent surfaces.
    ///
    ///     <para>Measured on a live village at 0.2m: anchor-reachable piece keys fell 825 to
    ///     625, regions kept 169 to 147, and a whole building went with them — the station
    ///     registry lost its workbench, stonecutter and spinning wheel. This test is the
    ///     tripwire for that exact change, and it fails in 80ms rather than three days later
    ///     in game.</para>
    /// </summary>
    [Fact]
    public void StairFlight_IsLost_AtTheBakeClimb_WhichIsWhyTheTwoDiffer()
    {
        var result = Building().Run(BakeClimb);

        Assert.False(result.Reached("stair"),
            "at the bake's step height the stairs are severed from their own floor — " +
            "this is the 825->625 piece-key regression, not an improvement");
        Assert.False(result.Reached("upper"),
            "and the storey above goes with them, taking its crafting stations");

        // The ground floor is never at risk — it is seeded directly, not chained onto.
        Assert.True(result.Reached("floor"));
    }

    // -------------------------------------------------------------------------
    // Rooftops: reachability is decided by the FLOOD, never by the link list.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     The invariant the deleted RoofIslandPrune violated. It read reachability off the
    ///     formal RegionLink list, which both misses edges the navmesh honours and invents
    ///     edges it does not — so it kept an unreachable roof and dropped a real building in
    ///     the same partition.
    ///
    ///     <para>Here a roof sits 4.5m above the floor in the SAME column. No amount of
    ///     linking elsewhere may make it reachable: only the height-gated flood decides.</para>
    /// </summary>
    [Fact]
    public void RoofAboveAFloor_IsNeverReached_HoweverItIsLinked()
    {
        var env = new PieceGridEnv()
            .Ground(0, 0, 0f, "terrain")
            .Ground(1, 0, 0f, "terrain")
            .PieceRun(0, 3, 0, 0.1f, "floor")
            // Directly overhead, a storey and a half up.
            .PieceRun(0, 3, 0, 4.5f, "roof");

        var result = env.Run(ShippedClimb);

        Assert.True(result.Reached("floor"));
        Assert.False(result.Reached("roof"),
            "a roof in the same column as the floor is not walkable from it");
        Assert.False(result.HasEdge("floor", "roof"));
    }

    // -------------------------------------------------------------------------
    // The perimeter gate.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Pieces whose cell Pass 1 classified as outside the wall ring must not be chained
    ///     into, even when they are flat neighbours at the same height. Without this gate the
    ///     piece chain hops the wall via 4-neighbour adjacency and the village annexes every
    ///     rogue floor and decoration beyond it.
    /// </summary>
    [Fact]
    public void PiecesBeyondThePerimeter_AreNotChainedInto()
    {
        var env = new PieceGridEnv()
            .Ground(0, 0, 0f, "terrain")
            .PieceRun(0, 1, 0, 0.1f, "floor")
            .Piece(2, 0, 0.1f, "outbuilding")
            .Outside(2, 0);

        var result = env.Run(ShippedClimb);

        Assert.True(result.Reached("floor"));
        Assert.False(result.Reached("outbuilding"),
            "a piece outside the perimeter is not village, however flat the step to it");
    }

    // -------------------------------------------------------------------------
    // A step is judged by the CELL, not by the region's average height.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     The failure that pruned a whole upper storey. RegionBuilder's coplanar merge turns a
    ///     stair flight into ONE region, and its centroid is the mean of the whole run — for a
    ///     flight climbing 36.5 to 39.0 that is 37.75, over a metre above the 36.5 tread a
    ///     villager actually steps onto from a 36.4 corridor.
    ///
    ///     <para>Gating on the centroid made a region's SIZE decide whether it could be
    ///     stepped onto, which is not a property of the step. Measured on the live village:
    ///     448 regions built, 119 committed; the building rendered its perfect upper floor and
    ///     then watched it get swept, because the only route up was through a flight whose
    ///     average height failed a 1m climb.</para>
    /// </summary>
    [Fact]
    public void AMergedFlight_IsJudgedByTheTreadYouStepOn_NotItsAverageHeight()
    {
        var env = new PieceGridEnv()
            .Ground(0, 0, 36.4f, "terrain")
            .PieceRun(0, 1, 0, 36.4f, "corridor")
            // One region, 36.5 -> 39.0, centroid at 37.75 — 1.35m above its own bottom tread.
            .MergedFlight(2, 5, 0, 36.5f, 39.0f, "flight")
            .PieceRun(6, 8, 0, 39.0f, "upper");

        var result = env.Run(ShippedClimb);

        Assert.True(result.Reached("flight"),
            "the bottom tread is 0.1m above the corridor; the flight's 37.75 average is not " +
            "what the villager steps onto");
        Assert.True(result.Reached("upper"),
            "and the storey the flight leads to comes back with it");
    }

    /// <summary>
    ///     The guard on that fix: judging by the cell must not turn into "any cell of a region
    ///     I can touch anywhere". A genuinely un-steppable riser is still un-steppable.
    /// </summary>
    [Fact]
    public void ACellTooHighToStepOnto_IsStillRefused()
    {
        var env = new PieceGridEnv()
            .Ground(0, 0, 0f, "terrain")
            .PieceRun(0, 1, 0, 0.1f, "floor")
            // Same region id, but the cell adjacent to the floor is 2.5m up.
            .MergedFlight(2, 4, 0, 2.6f, 4.0f, "ledge");

        var result = env.Run(ShippedClimb);

        Assert.True(result.Reached("floor"));
        Assert.False(result.Reached("ledge"),
            "a 2.5m riser is not a step, whatever the region's average height says");
    }

    // -------------------------------------------------------------------------
    // The live defect, reproduced headlessly.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     <b>Characterisation test — this pins a KNOWN DEFECT, not desired behaviour.</b>
    ///
    ///     <para>Narrow inner stairs running directly over a corridor, measured in game at
    ///     cell (-71,-373): terrain at 34.26, a <c>wood_floor</c> corridor at 36.3-36.5, the
    ///     stair treads at 39.0, and more structure above that — four walkable surfaces in one
    ///     XZ column. The flood keeps ONE height per column, and the step from the corridor
    ///     centroid to the stair centroid is about 2m, so the stairs are never claimed.</para>
    ///
    ///     <para>The visible symptom is worse than a missing region: because the stair cell
    ///     holds no region, <c>PointToRegionId</c> falls back to the adjacent height bucket and
    ///     returns the CORRIDOR region 2.6m below. Measured: a probe at y=39 and a probe at
    ///     y=36 both answered <c>p182</c>, whose own triangles are at Y=[36.34, 36.48]. So a
    ///     villager on the stairs reads as on the graph, in the wrong region, and every route
    ///     computed for it is a route through the corridor.</para>
    ///
    ///     <para>When the column model is fixed to carry stacked surfaces, this test will go
    ///     red. That is the point: change it deliberately, and make
    ///     <see cref="StairsOverACorridor_TheFixWouldLookLikeThis" /> the assertion.</para>
    /// </summary>
    [Fact]
    public void StairsOverACorridor_AreCurrentlyLost_KnownDefect()
    {
        var result = CorridorWithStairsOver().Run(ShippedClimb);

        Assert.True(result.Reached("corridor"), "the corridor itself is fine");
        Assert.False(result.Reached("stairs"),
            "KNOWN DEFECT: stairs stacked over a corridor are never claimed, because the " +
            "flood keeps one surface height per XZ column and the corridor wins it");
    }

    /// <summary>
    ///     The same geometry with the stairs displaced sideways instead of stacked. This is
    ///     the control: it proves the failure above is about STACKING, not about stairs, and
    ///     it is what the stacked case should look like once columns carry more than one
    ///     surface.
    /// </summary>
    [Fact]
    public void StairsOverACorridor_TheFixWouldLookLikeThis()
    {
        var env = new PieceGridEnv()
            .Ground(0, 0, 36.4f, "terrain")
            .PieceRun(0, 3, 0, 36.4f, "corridor")
            // Beside the corridor rather than on top of it: a normal reachable flight.
            .Piece(4, 0, 37.2f, "stairs")
            .Piece(5, 0, 38.1f, "stairs")
            .Piece(6, 0, 39.0f, "stairs");

        var result = env.Run(ShippedClimb);

        Assert.True(result.Reached("corridor"));
        Assert.True(result.Reached("stairs"),
            "the identical flight is reachable the moment it is not stacked in the same column");
    }

    // -------------------------------------------------------------------------
    // Guards.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     A null ground oracle skips the pass and yields empty results rather than throwing —
    ///     the same thing a null ZoneSystem did before the oracle was injected.
    /// </summary>
    [Fact]
    public void NoGroundOracle_SkipsThePass_Cleanly()
    {
        var env = new PieceGridEnv { GroundY = null };
        env.Ground(0, 0).PieceRun(0, 2, 0, 0.1f, "floor");

        var result = env.Run(ShippedClimb);

        Assert.Empty(result.ReachableKeys);
        Assert.Empty(result.Edges);
        Assert.Equal(0, result.PieceKeysAdded);
    }

    /// <summary>Every recorded edge joins two DIFFERENT regions, exactly once.</summary>
    [Fact]
    public void RecordedEdges_AreDeduplicated_AndNeverSelfLoops()
    {
        var result = Building().Run(ShippedClimb);

        Assert.All(result.Edges, e => Assert.NotEqual(e.fromRid, e.toRid));

        var seen = new HashSet<string>();
        foreach (var e in result.Edges)
        {
            var key = string.CompareOrdinal(e.fromRid, e.toRid) < 0
                ? e.fromRid + "|" + e.toRid
                : e.toRid + "|" + e.fromRid;
            Assert.True(seen.Add(key), $"duplicate edge {key}");
        }
    }

    // -------------------------------------------------------------------------
    // Fixtures.
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Ground floor at y=0.1, a stair flight whose single centroid sits at y=1.05 (the
    ///     measured shape: one region, centroid about a metre up), and an upper storey at
    ///     y=2.0 carrying the crafting stations.
    /// </summary>
    private static PieceGridEnv Building()
    {
        return new PieceGridEnv()
            .Ground(0, 0, 0f, "terrain")
            .Ground(1, 0, 0f, "terrain")
            .Ground(2, 0, 0f, "terrain")
            .PieceRun(0, 2, 0, 0.1f, "floor")
            .Piece(3, 0, 1.05f, "stair")
            .PieceRun(4, 6, 0, 2.0f, "upper");
    }

    /// <summary>
    ///     The measured (-71,-373) geometry: a corridor, and stairs in the SAME cells about
    ///     2.6m above it.
    /// </summary>
    private static PieceGridEnv CorridorWithStairsOver()
    {
        return new PieceGridEnv()
            .Ground(0, 0, 36.4f, "terrain")
            .PieceRun(0, 3, 0, 36.4f, "corridor")
            .PieceRun(1, 3, 0, 39.0f, "stairs");
    }
}
