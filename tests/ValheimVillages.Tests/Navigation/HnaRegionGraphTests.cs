using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.Navigation;

/// <summary>
/// Tests for RegionGraph instance methods: PointToRegionId, GetBoundaryCells, GetLinksFromRegion.
/// </summary>
public class RegionGraphTests
{
    private static RegionGraph BuildGraph(
        float originX, float originZ,
        HashSet<string> regionIds,
        List<HnaLink>? links = null,
        Dictionary<string, float>? cellHeights = null)
    {
        var graph = new RegionGraph();
        graph.SetGraph(originX, originZ, regionIds, links ?? new List<HnaLink>(),
            cellHeights ?? new Dictionary<string, float>());
        return graph;
    }

    #region PointToRegionId

    [Fact]
    public void PointToRegionId_ExactCellCenter_ReturnsCorrectRegion()
    {
        var regions = new HashSet<string> { "0_0_h5" };
        var graph = BuildGraph(0f, 0f, regions);

        // Cell center is at (0.5*3, 0.5*3) = (1.5, 1.5), height bucket 5 = y in [10, 12)
        string result = graph.PointToRegionId(new Vector3(1.5f, 10.5f, 1.5f));
        Assert.Equal("0_0_h5", result);
    }

    [Fact]
    public void PointToRegionId_FarOutside_ReturnsNull()
    {
        var regions = new HashSet<string> { "0_0_h0" };
        var graph = BuildGraph(0f, 0f, regions);

        string result = graph.PointToRegionId(new Vector3(100f, 0f, 100f));
        Assert.Null(result);
    }

    [Fact]
    public void PointToRegionId_WrongFloor_ReturnsNull_WithReducedRange()
    {
        // Cell at height bucket 0 (y=0..2), query at y=8 (bucket 4)
        // With ±1 search range, buckets 3..5 are checked — bucket 0 should NOT match
        var regions = new HashSet<string> { "0_0_h0" };
        var graph = BuildGraph(0f, 0f, regions);

        string result = graph.PointToRegionId(new Vector3(1.5f, 8f, 1.5f));
        Assert.Null(result);
    }

    [Fact]
    public void PointToRegionId_NearbyBucket_Matches()
    {
        // Cell at height bucket 5, query at y=11.9 (still bucket 5) — exact match
        var regions = new HashSet<string> { "0_0_h5" };
        var graph = BuildGraph(0f, 0f, regions);

        string result = graph.PointToRegionId(new Vector3(1.5f, 11.9f, 1.5f));
        Assert.Equal("0_0_h5", result);
    }

    [Fact]
    public void PointToRegionId_OneBucketAbove_Matches()
    {
        // Cell at bucket 5, query at bucket 4 (d=1 below) — should match
        var regions = new HashSet<string> { "0_0_h5" };
        var graph = BuildGraph(0f, 0f, regions);

        // y=9.5 → bucket 4, search checks bucket 4 (exact), then 5 (d=+1) → match
        string result = graph.PointToRegionId(new Vector3(1.5f, 9.5f, 1.5f));
        Assert.Equal("0_0_h5", result);
    }

    [Fact]
    public void PointToRegionId_UninitializedGraph_ReturnsNull()
    {
        var graph = new RegionGraph();
        Assert.Null(graph.PointToRegionId(new Vector3(0, 0, 0)));
    }

    #endregion

    #region GetBoundaryCells

    [Fact]
    public void GetBoundaryCells_SingleCellProtrusion_IsIncluded()
    {
        // 3x1 strip: cells (0,0), (1,0), (2,0)
        // Cell (2,0) is a single-cell protrusion on the right side.
        // After fix 2c, it should NOT be pruned.
        var regions = new HashSet<string> { "0_0_h0", "1_0_h0", "2_0_h0" };
        var heights = new Dictionary<string, float>
        {
            { "0_0_h0", 0f }, { "1_0_h0", 0f }, { "2_0_h0", 0f }
        };
        var graph = BuildGraph(0f, 0f, regions, cellHeights: heights);

        var boundary = graph.GetBoundaryCells();

        // All cells should be boundary (they all have exterior neighbors)
        Assert.Equal(3, boundary.Count);
        var ids = new HashSet<string>();
        foreach (var (cellId, _, _) in boundary)
            ids.Add(cellId);
        Assert.Contains("2_0_h0", ids);
    }

    [Fact]
    public void GetBoundaryCells_InteriorCell_IsExcluded()
    {
        // 3x3 block: the center cell (1,1) has no exterior neighbors
        var regions = new HashSet<string>();
        var heights = new Dictionary<string, float>();
        for (int x = 0; x < 3; x++)
        for (int z = 0; z < 3; z++)
        {
            string id = RegionGraph.CellKey(x, z, 0);
            regions.Add(id);
            heights[id] = 0f;
        }
        var graph = BuildGraph(0f, 0f, regions, cellHeights: heights);

        var boundary = graph.GetBoundaryCells();

        var ids = new HashSet<string>();
        foreach (var (cellId, _, _) in boundary)
            ids.Add(cellId);

        Assert.Equal(8, ids.Count);
        Assert.DoesNotContain("1_1_h0", ids);
    }

    [Fact]
    public void GetBoundaryCells_EmptyGraph_ReturnsEmpty()
    {
        var graph = new RegionGraph();
        Assert.Empty(graph.GetBoundaryCells());
    }

    #endregion

    #region GetLinksFromRegion

    [Fact]
    public void GetLinksFromRegion_BidirectionalSwap()
    {
        var regions = new HashSet<string> { "0_0_h0", "1_0_h0" };
        var links = new List<HnaLink>
        {
            new HnaLink
            {
                FromRegionId = "0_0_h0",
                ToRegionId = "1_0_h0",
                LinkType = HnaLinkType.Door,
                PositionStart = new Vector3(1, 0, 0),
                PositionEnd = new Vector3(2, 0, 0)
            }
        };
        var graph = BuildGraph(0f, 0f, regions, links);

        // Query from the From side
        var fromLinks = graph.GetLinksFromRegion("0_0_h0");
        Assert.Single(fromLinks);
        Assert.Equal("0_0_h0", fromLinks[0].FromRegionId);
        Assert.Equal("1_0_h0", fromLinks[0].ToRegionId);
        Assert.Equal(new Vector3(1, 0, 0), fromLinks[0].PositionStart);

        // Query from the To side — should get swapped endpoints
        var toLinks = graph.GetLinksFromRegion("1_0_h0");
        Assert.Single(toLinks);
        Assert.Equal("1_0_h0", toLinks[0].FromRegionId);
        Assert.Equal("0_0_h0", toLinks[0].ToRegionId);
        Assert.Equal(new Vector3(2, 0, 0), toLinks[0].PositionStart);
        Assert.Equal(new Vector3(1, 0, 0), toLinks[0].PositionEnd);
    }

    [Fact]
    public void GetLinksFromRegion_NoLinks_ReturnsEmpty()
    {
        var regions = new HashSet<string> { "0_0_h0" };
        var graph = BuildGraph(0f, 0f, regions);

        var links = graph.GetLinksFromRegion("0_0_h0");
        Assert.NotNull(links);
        Assert.Empty(links);
    }

    [Fact]
    public void GetLinksFromRegion_UnknownRegion_ReturnsEmpty()
    {
        var regions = new HashSet<string> { "0_0_h0" };
        var graph = BuildGraph(0f, 0f, regions);

        var links = graph.GetLinksFromRegion("99_99_h0");
        Assert.NotNull(links);
        Assert.Empty(links);
    }

    #endregion

    #region IsValidRegion / Clear

    [Fact]
    public void IsValidRegion_ReturnsTrueForKnownRegion()
    {
        var regions = new HashSet<string> { "0_0_h0" };
        var graph = BuildGraph(0f, 0f, regions);
        Assert.True(graph.IsValidRegion("0_0_h0"));
    }

    [Fact]
    public void IsValidRegion_ReturnsFalseAfterClear()
    {
        var regions = new HashSet<string> { "0_0_h0" };
        var graph = BuildGraph(0f, 0f, regions);
        graph.Clear();
        Assert.False(graph.IsValidRegion("0_0_h0"));
        Assert.False(graph.IsAvailable);
    }

    #endregion
}
