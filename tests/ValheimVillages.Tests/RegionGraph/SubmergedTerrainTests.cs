using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     A village built at the waterline must not claim the sea floor.
///
///     <para>Measured on the dedicated server (village e85d5819, a stone platform built out
///     over water): villager navmesh was baked on the sea bed — <c>SamplePosition: HIT</c> at
///     5.5m depth roughly 20m past the docks — and the region graph kept 274 regions of it.
///     Probes at three submerged cells all reported <c>in_outsideCells=NO</c>.</para>
///
///     <para>Two separate gaps produce that, one per pass, and these tests pin one each.
///     They are written against the OUTCOME ("the sea bed is not village"), not against a
///     particular remedy, so either candidate fix satisfies them.</para>
/// </summary>
public class SubmergedTerrainTests
{
    private const float WaterLevel = 30f;
    private const float MaxWadeDepth = 1f;

    /// <summary>The production predicate: deeper than wading depth is impassable.</summary>
    private static bool DeepWater(int gx, int gz, float y) => y < WaterLevel - MaxWadeDepth;

    /// <summary>Land out to gx=5, then open sea. No walls anywhere — the shore is the only edge.</summary>
    private static GridEnv Shoreline(int gxMax, int gzMax, int lastLandGx)
    {
        var env = new GridEnv();
        for (var gx = 0; gx <= gxMax; gx++)
        for (var gz = 0; gz <= gzMax; gz++)
            env.Height(gx, gz, gx <= lastLandGx ? 31f : 24f);
        return env;
    }

    [Fact]
    public void OpenSea_IsMarkedOutside_NotJustTheOneCellFringe()
    {
        // Deep water is a barrier to the perimeter flood, so the flood enters the sea
        // exactly one cell and stops. Everything past that is never REACHED — and a cell
        // the flood never reaches is never marked outside, which means it defaults to
        // INSIDE the village. The bake carves outside cells with phantom NotWalkable
        // sources, so an unmarked sea bed is laid with real walkable mesh.
        var env = Shoreline(12, 8, lastLandGx: 5);

        var result = new RubberBandPrune.FloodResult();
        RubberBandPrune.DrainNow(RubberBandPrune.PerimeterOutsideFlood(
            0, 0, 12, 8, env.CellY, env.WallBlocks, DeepWater, result));
        var outside = result.OutsideCells;

        // Controls that already hold: the first wet cell past the shore, and a sea cell
        // sitting on the bake-bounds border (every border cell is a perimeter seed).
        Assert.Contains(GridEnv.Key(6, 4), outside);
        Assert.Contains(GridEnv.Key(12, 4), outside);

        // The requirement: EVERY submerged cell is outside, however far from a shore or
        // a border it sits. Nothing can stand there, so none of it is village.
        for (var gx = 6; gx <= 12; gx++)
        for (var gz = 0; gz <= 8; gz++)
            Assert.Contains(GridEnv.Key(gx, gz), outside);
    }

    [Fact]
    public void AnchorOverSubmergedGround_DoesNotSeedTheFloodOnTheSeaBed()
    {
        // The registry anchor of e85d5819 stands on stone_floor_2x2 pieces at the
        // waterline, with solidHeight 48.79 in its column — but the cells beneath carry
        // only the terrain surface (traced: `surfaces=[27.88] pieceKeys=0`), so the one
        // surface the snap can choose is the sea bed metres below the anchor.
        //
        // The snap searches rings in XZ with no elevation test, so it accepts that cell
        // and Pass 2 seeds UNDER the water. That is why the submerged regions have no
        // walkable connection to the docks above them: the flood never came down from the
        // structure, it started at the bottom.
        var env = new GridEnv();
        for (var gx = 0; gx <= 6; gx++)
        for (var gz = 0; gz <= 6; gz++)
            env.Height(gx, gz, 24f); // the only recorded surface anywhere is sea bed

        var flood = new RubberBandPrune.FloodResult();
        RubberBandPrune.DrainNow(RubberBandPrune.PerimeterOutsideFlood(
            0, 0, 6, 6, env.CellY, env.WallBlocks, DeepWater, flood));

        var warnings = new List<string>();
        var anchors = new[] { new Vector3(3.5f, 40f, 3.5f) }; // on the platform, 16m up

        RubberBandPrune.AnchorReachableFlood(
            0, 0, 6, 6, anchors, flood.OutsideCells, flood.OutsideCellY,
            cell: 1f, anchorSnapRingMax: 6,
            env.IsPopulated, env.SurfacesAt, env.WallBlocks, maxClimb: 0.2f,
            out var reach, out _, out var seeds, out _, out _, out _, warnings.Add);

        // Nothing here is standable, so the village claims nothing — and says so, rather
        // than silently seeding 16m below the anchor.
        Assert.Equal(0, seeds);
        Assert.Empty(reach);
        Assert.NotEmpty(warnings);
    }
}
