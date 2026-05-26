using ValheimVillages.Villager.AI.Navigation;
using Xunit;

namespace ValheimVillages.Tests.Navigation;

/// <summary>
///     Tests for RegionGraph cell ID utilities: HeightBucket, TryParseCellId, CellKey.
/// </summary>
public class HnaCellUtilTests
{
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1.9f, 0)]
    [InlineData(2.0f, 1)]
    [InlineData(3.99f, 1)]
    [InlineData(4.0f, 2)]
    [InlineData(-1f, -1)]
    [InlineData(-2f, -1)]
    [InlineData(-2.01f, -2)]
    public void HeightBucket_ReturnsCorrectBucket(float y, int expectedBucket)
    {
        Assert.Equal(expectedBucket, RegionGraph.HeightBucket(y));
    }

    [Fact]
    public void HeightBucket_UsesConfiguredBucketSize()
    {
        var bucketSize = RegionGraph.HeightBucketSize;
        Assert.Equal(2f, bucketSize);
        Assert.Equal(1, RegionGraph.HeightBucket(bucketSize));
    }

    [Fact]
    public void TryParseCellId_ValidThreePart()
    {
        bool ok = RegionGraph.TryParseCellId("5_3_h7", out int ix, out int iz, out int hb);
        Assert.True(ok);
        Assert.Equal(5, ix);
        Assert.Equal(3, iz);
        Assert.Equal(7, hb);
    }

    [Fact]
    public void TryParseCellId_ValidTwoPart()
    {
        bool ok = RegionGraph.TryParseCellId("12_8", out int ix, out int iz, out int hb);
        Assert.True(ok);
        Assert.Equal(12, ix);
        Assert.Equal(8, iz);
        Assert.Equal(0, hb);
    }

    [Fact]
    public void TryParseCellId_NegativeIndices()
    {
        bool ok = RegionGraph.TryParseCellId("-2_-5_h3", out int ix, out int iz, out int hb);
        Assert.True(ok);
        Assert.Equal(-2, ix);
        Assert.Equal(-5, iz);
        Assert.Equal(3, hb);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("5")]
    [InlineData("x_y_h1")]
    public void TryParseCellId_RejectsMalformed(string id)
    {
        Assert.False(RegionGraph.TryParseCellId(id, out _, out _));
    }

    [Fact]
    public void TryParseCellId_RejectsNull()
    {
        Assert.False(RegionGraph.TryParseCellId(null, out _, out _));
    }

    [Theory]
    [InlineData(0, 0, 0, "0_0_h0")]
    [InlineData(5, 3, 7, "5_3_h7")]
    [InlineData(-1, -2, 4, "-1_-2_h4")]
    public void CellKey_FormatsCorrectly(int ix, int iz, int hb, string expected)
    {
        Assert.Equal(expected, RegionGraph.CellKey(ix, iz, hb));
    }

    [Theory]
    [InlineData(5, 3, 7)]
    [InlineData(0, 0, 0)]
    [InlineData(-1, -2, 4)]
    [InlineData(100, 200, 15)]
    public void CellKey_RoundTripsThroughParse(int ix, int iz, int hb)
    {
        string key = RegionGraph.CellKey(ix, iz, hb);
        bool ok = RegionGraph.TryParseCellId(key, out int pix, out int piz, out int phb);
        Assert.True(ok);
        Assert.Equal(ix, pix);
        Assert.Equal(iz, piz);
        Assert.Equal(hb, phb);
    }
}