using System;
using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Algorithms;
using Xunit;

namespace ValheimVillages.Tests.Algorithms;

public class ConnectedIslandsTests
{
    [Fact]
    public void SingleElement_ReturnsSingleIsland()
    {
        var islands = ConnectedIslands.FindIslands(
            1,
            isConnected: (a, b) => false,
            shouldTest: (a, b) => true);

        Assert.Single(islands);
        Assert.Equal(new[] { 0 }, islands[0]);
    }

    [Fact]
    public void AllConnected_ReturnsSingleIsland()
    {
        var islands = ConnectedIslands.FindIslands(
            5,
            isConnected: (a, b) => true,
            shouldTest: (a, b) => true);

        Assert.Single(islands);
        Assert.Equal(5, islands[0].Count);
    }

    [Fact]
    public void AllDisconnected_ReturnsNIslands()
    {
        var islands = ConnectedIslands.FindIslands(
            4,
            isConnected: (a, b) => false,
            shouldTest: (a, b) => true);

        Assert.Equal(4, islands.Count);
        foreach (var island in islands)
            Assert.Single(island);
    }

    [Fact]
    public void TwoClusters_ReturnsTwoIslands()
    {
        // 0-1-2 connected, 3-4 connected, no cross-cluster links
        var islands = ConnectedIslands.FindIslands(
            5,
            isConnected: (a, b) =>
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                return (lo < 3 && hi < 3) || (lo >= 3 && hi >= 3);
            },
            shouldTest: (a, b) => true);

        Assert.Equal(2, islands.Count);

        var sorted = islands.OrderBy(g => g[0]).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, sorted[0].OrderBy(x => x));
        Assert.Equal(new[] { 3, 4 }, sorted[1].OrderBy(x => x));
    }

    [Fact]
    public void ChainConnectivity_MergesTransitively()
    {
        // Only test adjacent pairs: 0-1, 1-2, 2-3
        // All connected via chain even though 0-3 is never tested directly
        var islands = ConnectedIslands.FindIslands(
            4,
            isConnected: (a, b) => Math.Abs(a - b) == 1,
            shouldTest: (a, b) => Math.Abs(a - b) <= 1);

        Assert.Single(islands);
        Assert.Equal(4, islands[0].Count);
    }

    [Fact]
    public void ShouldTestFilter_LimitsPairsChecked()
    {
        int callCount = 0;
        var islands = ConnectedIslands.FindIslands(
            4,
            isConnected: (a, b) => { callCount++; return true; },
            shouldTest: (a, b) => Math.Abs(a - b) == 1);

        // Only adjacent pairs tested: (0,1), (1,2), (2,3) = 3 calls
        // But union-find skips already-merged pairs, so at most 3
        Assert.True(callCount <= 3, $"Expected at most 3 calls, got {callCount}");
        Assert.Single(islands);
    }

    [Fact]
    public void ZeroElements_ReturnsEmpty()
    {
        var islands = ConnectedIslands.FindIslands(
            0,
            isConnected: (a, b) => true,
            shouldTest: (a, b) => true);

        Assert.Empty(islands);
    }

    [Fact]
    public void ThreeDisconnectedClusters()
    {
        // 0-1 connected, 2-3 connected, 4-5 connected
        var islands = ConnectedIslands.FindIslands(
            6,
            isConnected: (a, b) => (a / 2) == (b / 2),
            shouldTest: (a, b) => true);

        Assert.Equal(3, islands.Count);
        foreach (var island in islands)
            Assert.Equal(2, island.Count);
    }
}
