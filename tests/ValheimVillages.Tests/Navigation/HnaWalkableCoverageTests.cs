using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace ValheimVillages.Tests.Navigation;

/// <summary>
/// Verifies that every recorded walkable position in the test village
/// falls within a valid HNA region. The "golden" graph is built from
/// the recorded positions themselves, establishing the minimum set of
/// regions the flood-fill MUST produce for full coverage.
/// </summary>
public partial class HnaWalkableCoverageTests
{
    private readonly ITestOutputHelper _out;

    public HnaWalkableCoverageTests(ITestOutputHelper output) => _out = output;

    private static (float originX, float originZ) ComputeOrigin()
    {
        float minX = float.MaxValue, minZ = float.MaxValue;
        foreach (var p in WalkablePositions)
        {
            if (p.x < minX) minX = p.x;
            if (p.z < minZ) minZ = p.z;
        }
        return (minX, minZ);
    }

    /// <summary>
    /// Build an RegionGraph whose regions are exactly the cells
    /// the recorded walkable positions map to.
    /// </summary>
    private static (RegionGraph graph, HashSet<string> expectedRegions) BuildGoldenGraph()
    {
        var (originX, originZ) = ComputeOrigin();

        var regionIds = new HashSet<string>();
        var cellHeights = new Dictionary<string, float>();

        foreach (var p in WalkablePositions)
        {
            int ix = Mathf.FloorToInt((p.x - originX) / RegionGraph.CellSize);
            int iz = Mathf.FloorToInt((p.z - originZ) / RegionGraph.CellSize);
            int hb = RegionGraph.HeightBucket(p.y);
            string key = RegionGraph.CellKey(ix, iz, hb);
            regionIds.Add(key);
            if (!cellHeights.ContainsKey(key))
                cellHeights[key] = p.y;
        }

        var graph = new RegionGraph();
        graph.SetGraph(originX, originZ, regionIds, new List<HnaLink>(), cellHeights);
        return (graph, regionIds);
    }

    [Fact]
    public void AllWalkablePoints_FallWithinARegion()
    {
        var (graph, expectedRegions) = BuildGoldenGraph();
        var uncovered = new List<(int index, Vector3 position)>();

        for (int i = 0; i < WalkablePositions.Length; i++)
        {
            string region = graph.PointToRegionId(WalkablePositions[i]);
            if (region == null)
                uncovered.Add((i, WalkablePositions[i]));
        }

        _out.WriteLine($"Total positions: {WalkablePositions.Length}");
        _out.WriteLine($"Unique regions needed: {expectedRegions.Count}");
        _out.WriteLine($"Uncovered positions: {uncovered.Count}");

        foreach (var (idx, pos) in uncovered)
            _out.WriteLine($"  [{idx}] ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})");

        Assert.Empty(uncovered);
    }

    [Fact]
    public void GoldenGraph_RegionCatalog()
    {
        var (_, expectedRegions) = BuildGoldenGraph();

        _out.WriteLine($"Unique regions required for full coverage: {expectedRegions.Count}");
        foreach (var region in expectedRegions.OrderBy(r => r))
            _out.WriteLine($"  {region}");

        Assert.True(expectedRegions.Count >= 10,
            $"Expected at least 10 unique regions from {WalkablePositions.Length} walkable points, " +
            $"but got {expectedRegions.Count}");
    }

    [Fact]
    public void PointToRegionId_WithOffsetOrigin_StillCoversAllPoints()
    {
        var (baseOriginX, baseOriginZ) = ComputeOrigin();
        float originX = baseOriginX - 30f;
        float originZ = baseOriginZ - 30f;

        var regionIds = new HashSet<string>();
        var cellHeights = new Dictionary<string, float>();

        foreach (var p in WalkablePositions)
        {
            int ix = Mathf.FloorToInt((p.x - originX) / RegionGraph.CellSize);
            int iz = Mathf.FloorToInt((p.z - originZ) / RegionGraph.CellSize);
            int hb = RegionGraph.HeightBucket(p.y);
            string key = RegionGraph.CellKey(ix, iz, hb);
            regionIds.Add(key);
            if (!cellHeights.ContainsKey(key))
                cellHeights[key] = p.y;
        }

        var graph = new RegionGraph();
        graph.SetGraph(originX, originZ, regionIds, new List<HnaLink>(), cellHeights);

        var uncovered = new List<int>();
        for (int i = 0; i < WalkablePositions.Length; i++)
        {
            if (graph.PointToRegionId(WalkablePositions[i]) == null)
                uncovered.Add(i);
        }

        _out.WriteLine($"Origin offset by -30m: ({originX:F2}, {originZ:F2})");
        _out.WriteLine($"Regions with offset origin: {regionIds.Count}");
        _out.WriteLine($"Uncovered: {uncovered.Count}");

        Assert.Empty(uncovered);
    }

    [Fact]
    public void HeightBucketBoundary_AdjacentBucketStillResolves()
    {
        var (originX, originZ) = ComputeOrigin();

        var regionIds = new HashSet<string>();
        var cellHeights = new Dictionary<string, float>();

        foreach (var p in WalkablePositions)
        {
            int ix = Mathf.FloorToInt((p.x - originX) / RegionGraph.CellSize);
            int iz = Mathf.FloorToInt((p.z - originZ) / RegionGraph.CellSize);
            int hb = RegionGraph.HeightBucket(p.y);
            string key = RegionGraph.CellKey(ix, iz, hb);
            regionIds.Add(key);
            if (!cellHeights.ContainsKey(key))
                cellHeights[key] = p.y;
        }

        var graph = new RegionGraph();
        graph.SetGraph(originX, originZ, regionIds, new List<HnaLink>(), cellHeights);

        int resolvedViaAdjacentBucket = 0;
        int totalTested = 0;

        foreach (var p in WalkablePositions)
        {
            float nudgedY = p.y + 1.5f;
            int exactBucket = RegionGraph.HeightBucket(nudgedY);
            int originalBucket = RegionGraph.HeightBucket(p.y);

            if (exactBucket == originalBucket)
                continue;

            totalTested++;
            var nudgedPos = new Vector3(p.x, nudgedY, p.z);
            string region = graph.PointToRegionId(nudgedPos);
            if (region != null)
                resolvedViaAdjacentBucket++;
        }

        _out.WriteLine($"Points with nudged Y crossing bucket boundary: {totalTested}");
        _out.WriteLine($"Resolved via ±1 bucket search: {resolvedViaAdjacentBucket}");

        Assert.True(resolvedViaAdjacentBucket > 0,
            "Expected at least some points to resolve via adjacent bucket search");
    }

    [Fact]
    public void WalkableData_HasExpectedPointCount()
    {
        Assert.Equal(389, WalkablePositions.Length);
    }

    [Fact]
    public void WalkablePositions_SpanMultipleHeightBuckets()
    {
        var buckets = new HashSet<int>();
        foreach (var p in WalkablePositions)
            buckets.Add(RegionGraph.HeightBucket(p.y));

        _out.WriteLine($"Height buckets spanned: {string.Join(", ", buckets.OrderBy(b => b))}");

        Assert.True(buckets.Count >= 2,
            "Walkable path should span at least 2 height buckets (multi-floor village)");
    }
}
