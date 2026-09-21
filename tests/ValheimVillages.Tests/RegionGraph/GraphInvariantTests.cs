using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;
using RegionGraphType = ValheimVillages.Villager.AI.Navigation.RegionGraph;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     A prune that removes regions must remove EVERYTHING that referenced them, in the same
///     pass. When it does not, the leftovers are worse than the prune never running: a lookup
///     cell still pointing at a dropped region makes <c>PointToRegionId</c> answer with an id
///     that has no links and no centroid, and every caller that tests "did I get a region id?"
///     concludes a villager standing there is safely on the graph.
///
///     <para>Measured: after the first cut of the (since deleted) roof-island prune, a roof
///     kept resolving to <c>p1101</c> with <c>links=0 regionValid=False</c>. These tests are
///     that bug, and the near misses around it, at 80ms.</para>
/// </summary>
public class GraphInvariantTests : IDisposable
{
    public GraphInvariantTests() => RegionGraphType.StrictInvariants = true;

    public void Dispose() => RegionGraphType.StrictInvariants = false;

    private static Vector3 At(float x, float y, float z) => new(x, y, z);

    /// <summary>A minimal well-formed graph: two regions, one link, cells for both.</summary>
    private static (HashSet<string> ids, List<RegionLink> links,
        Dictionary<string, Vector3> centroids, Dictionary<long, string> lookup)
        Wellformed()
    {
        var ids = new HashSet<string> { "a", "b" };
        var links = new List<RegionLink>
        {
            new() { FromRegionId = "a", ToRegionId = "b", PositionStart = At(0, 0, 0), PositionEnd = At(1, 0, 0) },
        };
        var centroids = new Dictionary<string, Vector3> { ["a"] = At(0, 0, 0), ["b"] = At(1, 0, 0) };
        var lookup = new Dictionary<long, string>
        {
            [RegionGraphType.PackLookup(0, 0, 0)] = "a",
            [RegionGraphType.PackLookup(1, 0, 0)] = "b",
        };
        return (ids, links, centroids, lookup);
    }

    [Fact]
    public void AWellFormedGraph_Commits()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        var graph = new RegionGraphType();

        graph.SetGraph(ids, links, centroids, lookup, null, null);

        Assert.Equal(2, graph.RegionCount);
        Assert.Equal("a", graph.PointToRegionId(At(0.5f, 0f, 0.5f)));
    }

    /// <summary>
    ///     The p1101 ghost itself: the region is gone from every structure EXCEPT the lookup
    ///     grid, so position queries still answer with it.
    /// </summary>
    [Fact]
    public void ALookupCellPointingAtADroppedRegion_IsRejected()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        lookup[RegionGraphType.PackLookup(9, 9, 2)] = "roof"; // never in ids

        var graph = new RegionGraphType();
        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.SetGraph(ids, links, centroids, lookup, null, null));
        Assert.Contains("not in the graph", ex.Message);
    }

    [Fact]
    public void ALinkToADroppedRegion_IsRejected()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        links.Add(new RegionLink { FromRegionId = "a", ToRegionId = "roof" });

        var graph = new RegionGraphType();
        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.SetGraph(ids, links, centroids, lookup, null, null));
        Assert.Contains("referenced missing regions", ex.Message);
    }

    /// <summary>
    ///     A region with no centroid cannot be scored, seeded or linked from. This is the
    ///     fault one step EARLIER than the dangling lookup cell — the ghost had both, and only
    ///     the later one was checked.
    /// </summary>
    [Fact]
    public void ARegionWithNoCentroid_IsRejected()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        ids.Add("c");
        lookup[RegionGraphType.PackLookup(2, 0, 0)] = "c";
        // centroids deliberately not given one

        var graph = new RegionGraphType();
        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.SetGraph(ids, links, centroids, lookup, null, null));
        Assert.Contains("no centroid", ex.Message);
    }

    /// <summary>
    ///     The mirror image: a region nothing can ever resolve to. It inflates the region
    ///     count, so a partition that half-pruned still looks healthy by the numbers.
    /// </summary>
    [Fact]
    public void ARegionWithNoLookupCell_IsRejected()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        ids.Add("c");
        centroids["c"] = At(2, 0, 0);
        // no lookup cell names "c"

        var graph = new RegionGraphType();
        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.SetGraph(ids, links, centroids, lookup, null, null));
        Assert.Contains("no lookup cell", ex.Message);
    }

    [Fact]
    public void AKindEntryForADroppedRegion_IsRejected()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        var kinds = new Dictionary<string, SurfaceKind>
        {
            ["a"] = SurfaceKind.Piece,
            ["b"] = SurfaceKind.Terrain,
            ["roof"] = SurfaceKind.Piece,
        };

        var graph = new RegionGraphType();
        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.SetGraph(ids, links, centroids, lookup, null, kinds));
        Assert.Contains("kind entr", ex.Message);
    }

    [Fact]
    public void ASelfLink_IsRejected()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        links.Add(new RegionLink { FromRegionId = "a", ToRegionId = "a" });

        var graph = new RegionGraphType();
        var ex = Assert.Throws<InvalidOperationException>(
            () => graph.SetGraph(ids, links, centroids, lookup, null, null));
        Assert.Contains("itself", ex.Message);
    }

    /// <summary>
    ///     Door links are appended AFTER the graph is built, against regions a later prune may
    ///     have removed. That path used to skip validation entirely.
    /// </summary>
    [Fact]
    public void AppendedDoorLinks_AreValidatedToo()
    {
        var (ids, links, centroids, lookup) = Wellformed();
        var graph = new RegionGraphType();
        graph.SetGraph(ids, links, centroids, lookup, null, null);

        var ex = Assert.Throws<InvalidOperationException>(() => graph.AddLinks(
            new List<RegionLink> { new() { FromRegionId = "a", ToRegionId = "vanished" } }));
        Assert.Contains("referenced missing regions", ex.Message);
    }

    /// <summary>
    ///     Off the strict flag — the shipping behaviour — an inconsistent graph must still
    ///     COMMIT and must be self-consistent afterwards. A village that half-pruned is still
    ///     a village; taking it offline helps nobody, but the ghost must be gone.
    /// </summary>
    [Fact]
    public void WithoutStrictInvariants_TheGraphRepairsItselfAndTheGhostStopsAnswering()
    {
        RegionGraphType.StrictInvariants = false;

        var (ids, links, centroids, lookup) = Wellformed();
        var ghostCell = RegionGraphType.PackLookup(9, 9, 2);
        lookup[ghostCell] = "roof";
        links.Add(new RegionLink { FromRegionId = "a", ToRegionId = "roof" });

        var graph = new RegionGraphType();
        graph.SetGraph(ids, links, centroids, lookup, null, null);

        Assert.Equal(2, graph.RegionCount);
        Assert.Equal(1, graph.LinkCount);
        Assert.Null(graph.PointToRegionId(At(9.5f, 4.5f, 9.5f)));
    }
}
