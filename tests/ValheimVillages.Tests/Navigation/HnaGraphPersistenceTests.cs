using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.Navigation;

/// <summary>
/// Tests for HNA graph serialization and restoration (ZDO persistence).
/// </summary>
public class RegionGraphPersistenceTests
{
    private static RegionGraph BuildTestGraph(
        float originX = 0f, float originZ = 0f,
        HashSet<string>? regionIds = null,
        List<HnaLink>? links = null,
        Dictionary<string, float>? cellHeights = null,
        Dictionary<string, Vector2>? nudgedXZ = null)
    {
        var graph = new RegionGraph();
        regionIds ??= new HashSet<string> { "0_0_h0", "1_0_h0", "0_1_h0" };
        links ??= new List<HnaLink>();
        cellHeights ??= new Dictionary<string, float>
        {
            { "0_0_h0", 10f }, { "1_0_h0", 10.5f }, { "0_1_h0", 11f }
        };
        graph.SetGraph(originX, originZ, regionIds, links, cellHeights, nudgedXZ);
        return graph;
    }

    [Fact]
    public void SerializeRestore_RoundTrip_PreservesRegions()
    {
        var original = BuildTestGraph(originX: 5f, originZ: 10f);
        string data = original.Serialize();
        Assert.False(string.IsNullOrEmpty(data));

        var restored = new RegionGraph();
        bool ok = restored.Restore(data);

        Assert.True(ok);
        Assert.Equal(original.RegionCount, restored.RegionCount);
        Assert.True(restored.IsAvailable);
    }

    [Fact]
    public void SerializeRestore_RoundTrip_PreservesOrigin()
    {
        var original = BuildTestGraph(originX: 42.5f, originZ: -17.3f);
        string data = original.Serialize();

        var restored = new RegionGraph();
        restored.Restore(data);

        Assert.True(restored.GetOrigin(out float ox, out float oz));
        Assert.InRange(ox, 42.4f, 42.6f);
        Assert.InRange(oz, -17.4f, -17.2f);
    }

    [Fact]
    public void SerializeRestore_RoundTrip_PreservesCellHeights()
    {
        var original = BuildTestGraph();
        string data = original.Serialize();

        var restored = new RegionGraph();
        restored.Restore(data);

        Assert.True(restored.TryGetCellHeight("0_0_h0", out float h));
        Assert.InRange(h, 9.99f, 10.01f);
    }

    [Fact]
    public void SerializeRestore_RoundTrip_PreservesLinks()
    {
        var links = new List<HnaLink>
        {
            new HnaLink
            {
                FromRegionId = "0_0_h0",
                ToRegionId = "1_0_h0",
                LinkType = HnaLinkType.Door,
                PositionStart = new Vector3(1f, 10f, 0f),
                PositionEnd = new Vector3(2f, 10f, 0f)
            },
            new HnaLink
            {
                FromRegionId = "0_0_h0",
                ToRegionId = "0_1_h0",
                LinkType = HnaLinkType.Slope,
                PositionStart = new Vector3(0f, 10f, 1f),
                PositionEnd = new Vector3(0f, 11f, 2f)
            }
        };
        var original = BuildTestGraph(links: links);
        string data = original.Serialize();

        var restored = new RegionGraph();
        restored.Restore(data);

        Assert.Equal(2, restored.LinkCount);
        var restoredLinks = restored.GetAllLinks();
        Assert.Equal(HnaLinkType.Door, restoredLinks[0].LinkType);
        Assert.Equal(HnaLinkType.Slope, restoredLinks[1].LinkType);
        Assert.Equal("0_0_h0", restoredLinks[0].FromRegionId);
        Assert.Equal("1_0_h0", restoredLinks[0].ToRegionId);
    }

    [Fact]
    public void Restore_RejectsMismatchedCellSize()
    {
        // Manually craft a serialized string with a different cell size
        string data = "0.00,0.00,5.00;0_0_h0:10.00";

        var graph = new RegionGraph();
        bool ok = graph.Restore(data);

        Assert.False(ok);
        Assert.False(graph.IsAvailable);
    }

    [Fact]
    public void Restore_HandlesEmptyLinkSection()
    {
        var original = BuildTestGraph();
        string data = original.Serialize();

        // The serialized data should NOT contain || if there are no links
        Assert.DoesNotContain("||", data);

        var restored = new RegionGraph();
        bool ok = restored.Restore(data);

        Assert.True(ok);
        Assert.Equal(0, restored.LinkCount);
    }

    [Fact]
    public void SerializeRestore_PreservesNudgedXZ()
    {
        var nudged = new Dictionary<string, Vector2>
        {
            { "0_0_h0", new Vector2(1.5f, 2.5f) }
        };
        var original = BuildTestGraph(nudgedXZ: nudged);
        string data = original.Serialize();

        var restored = new RegionGraph();
        restored.Restore(data);

        Assert.True(restored.TryGetNudgedXZ("0_0_h0", out Vector2 nxz));
        Assert.InRange(nxz.x, 1.49f, 1.51f);
        Assert.InRange(nxz.y, 2.49f, 2.51f);
    }

    [Fact]
    public void Restore_RejectsEmptyString()
    {
        var graph = new RegionGraph();
        Assert.False(graph.Restore(""));
        Assert.False(graph.Restore(null));
    }

    [Fact]
    public void Serialize_UnavailableGraph_ReturnsEmpty()
    {
        var graph = new RegionGraph();
        Assert.Equal("", graph.Serialize());
    }
}
