using ValheimVillages.Tags;
using Xunit;

namespace ValheimVillages.Tests.Definitions;

/// <summary>
/// Tests for the namespace:value tag parser and filter utilities.
/// </summary>
public class TagParserTests
{
    [Theory]
    [InlineData("behavior:patrol", "behavior", "patrol")]
    [InlineData("listpanel:guardstatus", "listpanel", "guardstatus")]
    [InlineData("tab:workorder", "tab", "workorder")]
    [InlineData("ability:mountainstride", "ability", "mountainstride")]
    public void TryParse_ValidTag_ReturnsCorrectNamespaceAndValue(
        string tag, string expectedNs, string expectedValue)
    {
        Assert.True(TagParser.TryParse(tag, out var ns, out var value));
        Assert.Equal(expectedNs, ns);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nocolon")]
    [InlineData(":nonamespace")]
    [InlineData("novalue:")]
    public void TryParse_InvalidTag_ReturnsFalse(string? tag)
    {
        Assert.False(TagParser.TryParse(tag!, out _, out _));
    }

    [Fact]
    public void FilterByNamespace_ReturnsOnlyMatchingTags()
    {
        var tags = new[] { "behavior:patrol", "behavior:craft", "ability:mountainstride", "tab:info" };
        var result = TagParser.FilterByNamespace(tags, "behavior");

        Assert.Equal(2, result.Count);
        Assert.Contains("behavior:patrol", result);
        Assert.Contains("behavior:craft", result);
    }

    [Fact]
    public void GetValues_ReturnsValuesForNamespace()
    {
        var tags = new[] { "behavior:patrol", "behavior:craft", "ability:mountainstride" };
        var result = TagParser.GetValues(tags, "behavior");

        Assert.Equal(2, result.Count);
        Assert.Contains("patrol", result);
        Assert.Contains("craft", result);
    }

    [Fact]
    public void HasTag_MatchesExactNamespaceAndValue()
    {
        var tags = new[] { "behavior:patrol", "ability:mountainstride" };

        Assert.True(TagParser.HasTag(tags, "behavior", "patrol"));
        Assert.False(TagParser.HasTag(tags, "behavior", "craft"));
        Assert.False(TagParser.HasTag(tags, "unknown", "patrol"));
    }

    [Fact]
    public void HasTag_CaseInsensitive()
    {
        var tags = new[] { "Behavior:Patrol" };
        Assert.True(TagParser.HasTag(tags, "behavior", "patrol"));
        Assert.True(TagParser.HasTag(tags, "BEHAVIOR", "PATROL"));
    }

    [Fact]
    public void HasNamespace_ReturnsTrueWhenAnyValueExists()
    {
        var tags = new[] { "behavior:patrol", "ability:mountainstride" };

        Assert.True(TagParser.HasNamespace(tags, "behavior"));
        Assert.True(TagParser.HasNamespace(tags, "ability"));
        Assert.False(TagParser.HasNamespace(tags, "contextmenu"));
    }

    [Fact]
    public void GetNamespace_ReturnsNamespace()
    {
        Assert.Equal("behavior", TagParser.GetNamespace("behavior:patrol"));
        Assert.Null(TagParser.GetNamespace("invalid"));
    }

    [Fact]
    public void GetValue_ReturnsValue()
    {
        Assert.Equal("patrol", TagParser.GetValue("behavior:patrol"));
        Assert.Null(TagParser.GetValue("invalid"));
    }
}
