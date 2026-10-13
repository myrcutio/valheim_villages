using ValheimVillages.Abilities.FearBadges;
using Xunit;

namespace ValheimVillages.Tests.Definitions;

/// <summary>
///     Pins how fear wards are encoded onto the player's ZDO.
///
///     <para>Worth pinning because every failure here is silent. The wards share one
///     comma-separated ZDO field, so a name carrying the separator would round-trip as two
///     wards that match no creature, and an uncapped list would grow a replicated field without
///     bound — both of which present in-world as "the badge does nothing", indistinguishable
///     from the AI patch not firing.</para>
/// </summary>
public class FearBadgeStoreTests
{
    [Fact]
    public void RoundTrip_PreservesWards()
    {
        var encoded = string.Join(",", FearBadgeStore.Sanitise(new[] { "Troll", "Greyling" }));
        var wards = FearBadgeStore.ParseWards(encoded);

        Assert.Equal(2, wards.Count);
        Assert.Contains("Troll", wards);
        Assert.Contains("Greyling", wards);
    }

    [Fact]
    public void Sanitise_DropsNamesCarryingTheSeparator()
    {
        // "Troll,Greyling" as a single name would decode as two wards, neither real.
        var kept = FearBadgeStore.Sanitise(new[] { "Troll,Greyling", "Boar" });

        Assert.Equal(new[] { "Boar" }, kept);
    }

    [Fact]
    public void Sanitise_DropsEmptyAndWhitespace()
    {
        var kept = FearBadgeStore.Sanitise(new[] { "", "   ", null, "Boar" });
        Assert.Equal(new[] { "Boar" }, kept);
    }

    [Fact]
    public void Sanitise_Deduplicates()
    {
        var kept = FearBadgeStore.Sanitise(new[] { "Troll", "Troll", "Troll" });
        Assert.Equal(new[] { "Troll" }, kept);
    }

    [Fact]
    public void Sanitise_TrimsSurroundingWhitespace()
    {
        // Parse trims on the way out, so Sanitise must trim on the way in or " Troll" and
        // "Troll" would both be stored and only one would ever match.
        var kept = FearBadgeStore.Sanitise(new[] { " Troll ", "Troll" });
        Assert.Equal(new[] { "Troll" }, kept);
    }

    [Fact]
    public void Sanitise_EnforcesTheCap()
    {
        var many = new string[FearBadgeStore.MaxBadges + 10];
        for (var i = 0; i < many.Length; i++) many[i] = $"Creature{i}";

        Assert.Equal(FearBadgeStore.MaxBadges, FearBadgeStore.Sanitise(many).Count);
    }

    [Fact]
    public void ParseWards_EmptyFieldMeansNoWards()
    {
        // An unset ZDO string reads as "", which must mean no wards rather than one blank ward
        // that could match a creature whose prefab name failed to resolve.
        Assert.Empty(FearBadgeStore.ParseWards(""));
        Assert.Empty(FearBadgeStore.ParseWards(null));
    }

    [Fact]
    public void ParseWards_IgnoresStraySeparators()
    {
        var wards = FearBadgeStore.ParseWards(",Troll,,Boar,");
        Assert.Equal(2, wards.Count);
        Assert.Contains("Troll", wards);
        Assert.Contains("Boar", wards);
    }

    [Fact]
    public void FearRange_IsFiniteAndNotCrownMode()
    {
        // Crown mode's 9999f would empty the map instead of clearing a path; the design calls
        // for a finite range and this is the only place that choice is recorded in code.
        Assert.True(FearBadgeStore.FearRange > 0f);
        Assert.True(FearBadgeStore.FearRange < 100f,
            $"{FearBadgeStore.FearRange}m is approaching crown mode's 9999f blanket fear");
    }
}
