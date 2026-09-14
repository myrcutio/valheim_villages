using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Scheduling;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;
using Graph = ValheimVillages.Villager.AI.Navigation.RegionGraph;

namespace ValheimVillages.Tests.Scheduling;

/// <summary>
///     <see cref="RegionHopDistance" /> is the scheduler's only distance signal, and its
///     return value is a hard filter in <see cref="TaskReranker" />. These lock down the
///     regression that made a villager starve: an endpoint that fails to resolve to a
///     region used to return -1, which dropped EVERY candidate and left the villager with
///     no work forever (a Farmer standing 0.16m from an indexed cell, reading as
///     region=UNRESOLVED, with 65 tasks on the board).
/// </summary>
public class RegionHopDistanceTests
{
    /// <summary>
    ///     A graph with a single region covering a 4x4m patch of lookup cells at ground
    ///     level. Everything outside that patch is deliberately unindexed, so it reproduces
    ///     the "on the mesh but not in the lookup grid" hole.
    /// </summary>
    private static Graph PatchAtOrigin(string regionId = "r1")
    {
        var lookup = new Dictionary<long, string>();
        var hb = Graph.HeightBucket(0f);
        for (var gx = 0; gx < 4; gx++)
        for (var gz = 0; gz < 4; gz++)
            lookup[Graph.PackLookup(gx, gz, hb)] = regionId;

        var graph = new Graph();
        graph.SetGraph(
            new HashSet<string> { regionId },
            new List<RegionLink>(),
            new Dictionary<string, Vector3> { [regionId] = new(2f, 0f, 2f) },
            lookup);
        return graph;
    }

    private static Vector3 InPatch => new(1.5f, 0f, 1.5f);

    /// <summary>Far outside the indexed patch AND outside the 6m lookup-snap fallback.</summary>
    private static Vector3 OffGraph => new(500f, 0f, 500f);

    [Fact]
    public void NullGraph_IsTheOnlyMinusOne()
    {
        Assert.Equal(-1, RegionHopDistance.Hops(null, InPatch, InPatch));
    }

    [Fact]
    public void SameRegion_IsZeroHops()
    {
        var graph = PatchAtOrigin();
        Assert.Equal(0, RegionHopDistance.Hops(graph, new Vector3(0.5f, 0f, 0.5f), new Vector3(3.5f, 0f, 3.5f)));
    }

    [Fact]
    public void UnresolvedSource_StillScores_SoTheVillagerIsNeverStarved()
    {
        var graph = PatchAtOrigin();

        // The villager is off-graph; the task is a normal in-village row. This is the exact
        // shape that used to filter out every candidate.
        var hops = RegionHopDistance.Hops(graph, OffGraph, InPatch);

        Assert.True(hops >= 0, $"an unresolved SOURCE must not read as unreachable (got {hops})");
    }

    [Fact]
    public void UnresolvedTarget_StillScores()
    {
        var graph = PatchAtOrigin();

        var hops = RegionHopDistance.Hops(graph, InPatch, OffGraph);

        Assert.True(hops >= 0, $"an unresolved TARGET must not read as unreachable (got {hops})");
    }

    [Fact]
    public void BothUnresolved_AreNotTreatedAsCoLocated()
    {
        var graph = PatchAtOrigin();
        var a = OffGraph;
        var b = OffGraph + new Vector3(90f, 0f, 0f);

        // Two points that both fail to resolve must not collapse to "same region, 0 hops" —
        // that would make every far-away task look like it was underfoot.
        Assert.Equal(30, RegionHopDistance.Hops(graph, a, b)); // 90m / CellSize(3m)
    }

    [Fact]
    public void Distance_ScalesWithCellSize()
    {
        var graph = PatchAtOrigin();
        var from = InPatch;
        var to = from + new Vector3(Graph.CellSize * 7f, 0f, 0f);

        Assert.Equal(7, RegionHopDistance.Hops(graph, from, to));
    }

    [Fact]
    public void AdjacentButUnindexedPoint_ResolvesViaTheSnapFallback()
    {
        var graph = PatchAtOrigin();

        // 2m outside the indexed patch: PointToRegionId misses, but the 6m
        // TryFindNearestLookupCell fallback should still land it in r1 — so it reads as the
        // SAME region as a point inside, not as a separate place.
        Assert.Equal(0, RegionHopDistance.Hops(graph, new Vector3(5.5f, 0f, 1.5f), InPatch));
    }
}
