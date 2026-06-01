using System;
using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Algorithms;
using Xunit;

namespace ValheimVillages.Tests.RegionGraph;

/// <summary>
///     Region-linking pass: <see cref="ConnectedIslands.FindIslands" /> is the
///     Union-Find that groups regions (and, in <c>NavMeshLinkPlacer</c>, link
///     candidates) into connected components from a caller-supplied connectivity
///     predicate. Driven here with synthetic predicates so the component logic —
///     transitivity, the <c>shouldTest</c> pruning gate, and component
///     boundaries — is verified without any geometry.
/// </summary>
public class RegionLinkingTests
{
    private static readonly Func<int, int, bool> Always = (_, _) => true;

    /// <summary>Normalize to a set of sorted member-lists so assertions are order-independent.</summary>
    private static HashSet<string> Normalize(List<List<int>> islands) =>
        islands.Select(g => string.Join(",", g.OrderBy(x => x))).ToHashSet();

    [Fact]
    public void ZeroElements_ReturnsNoIslands()
    {
        Assert.Empty(ConnectedIslands.FindIslands(0, Always, Always));
    }

    [Fact]
    public void SingleElement_IsItsOwnIsland()
    {
        var islands = ConnectedIslands.FindIslands(1, Always, Always);
        var only = Assert.Single(islands);
        Assert.Equal(new[] { 0 }, only);
    }

    [Fact]
    public void AllPairsConnected_MergeIntoOneIsland()
    {
        var islands = ConnectedIslands.FindIslands(5, Always, Always);
        var only = Assert.Single(islands);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, only.OrderBy(x => x));
    }

    [Fact]
    public void NothingConnected_EveryElementIsolated()
    {
        var islands = ConnectedIslands.FindIslands(4, (_, _) => false, Always);
        Assert.Equal(4, islands.Count);
        Assert.All(islands, g => Assert.Single(g));
    }

    [Fact]
    public void TwoClusters_SeparatedByConnectivity()
    {
        // {0,1,2} mutually connected, {3,4} mutually connected, no cross edges.
        bool Connected(int a, int b) => (a < 3) == (b < 3);
        var islands = ConnectedIslands.FindIslands(5, Connected, Always);

        Assert.Equal(new HashSet<string> { "0,1,2", "3,4" }, Normalize(islands));
    }

    [Fact]
    public void TransitiveUnion_ChainGateStillMergesAll()
    {
        // shouldTest only admits adjacent pairs (a chain), and adjacents are
        // connected. Union-Find must still collapse the whole chain into one
        // component via transitivity — no need to test the far-apart pairs.
        bool Adjacent(int a, int b) => Math.Abs(a - b) == 1;
        var islands = ConnectedIslands.FindIslands(6, Adjacent, Adjacent);

        var only = Assert.Single(islands);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, only.OrderBy(x => x));
    }

    [Fact]
    public void ShouldTestGate_PreventsOtherwiseConnectingEdges()
    {
        // Everything *would* connect, but the gate forbids testing any pair
        // bridging the 0-2 / 3-5 halves, so they stay separate components.
        bool SameHalf(int a, int b) => (a < 3) == (b < 3);
        var islands = ConnectedIslands.FindIslands(6, Always, SameHalf);

        Assert.Equal(new HashSet<string> { "0,1,2", "3,4,5" }, Normalize(islands));
    }
}
