using ValheimVillages.Behaviors.Relax;
using Xunit;

namespace ValheimVillages.Tests.Definitions;

/// <summary>
///     Pins the drive packing used to replicate mood from a villager's owner to other peers.
///
///     <para>Worth pinning because the failure is silent and plausible-looking: four values
///     sharing one long means a shift or mask slip swaps two drives rather than erroring, and
///     the panel would confidently report the wrong mood. The high byte in particular is where
///     a sign-extension bug would land.</para>
/// </summary>
public class VillagerDriveMirrorTests
{
    private static DriveState RoundTrip(float warmth, float social, float rest, float curiosity)
    {
        return VillagerDriveMirror.Unpack(VillagerDriveMirror.Pack(new DriveState
        {
            Warmth = warmth, Social = social, Rest = rest, Curiosity = curiosity
        }));
    }

    [Fact]
    public void RoundTrip_KeepsEachDriveInItsOwnSlot()
    {
        // Deliberately all different, so a swapped shift cannot pass.
        var d = RoundTrip(0.00f, 0.25f, 0.50f, 1.00f);

        Assert.Equal(0.00f, d.Warmth, 0.01);
        Assert.Equal(0.25f, d.Social, 0.01);
        Assert.Equal(0.50f, d.Rest, 0.01);
        Assert.Equal(1.00f, d.Curiosity, 0.01);
    }

    [Fact]
    public void RoundTrip_SurvivesTheTopByteBeingFull()
    {
        // Curiosity occupies the high byte: 1.0 there is 0xFF << 24, which is exactly the
        // value that sign-extends to a negative int if packed through the wrong type.
        var d = RoundTrip(0f, 0f, 0f, 1f);

        Assert.Equal(1f, d.Curiosity, 0.01);
        Assert.Equal(0f, d.Warmth, 0.01);
        Assert.True(VillagerDriveMirror.Pack(new DriveState { Curiosity = 1f }) > 0,
            "a full top byte must stay positive so -1 remains usable as the absent sentinel");
    }

    [Fact]
    public void RoundTrip_AllFull()
    {
        var d = RoundTrip(1f, 1f, 1f, 1f);
        Assert.Equal(1f, d.Warmth, 0.01);
        Assert.Equal(1f, d.Social, 0.01);
        Assert.Equal(1f, d.Rest, 0.01);
        Assert.Equal(1f, d.Curiosity, 0.01);
    }

    [Fact]
    public void Pack_NeverCollidesWithTheAbsentSentinel()
    {
        // ForDisplay treats -1 as "the owner has not published yet". No real state may pack
        // to it, or a villager would read as unknown forever.
        for (var i = 0; i <= 10; i++)
        {
            var v = i / 10f;
            Assert.NotEqual(-1L, VillagerDriveMirror.Pack(new DriveState
            {
                Warmth = v, Social = v, Rest = v, Curiosity = v
            }));
        }
    }

    [Theory]
    [InlineData(-5f, 0f)] // below range clamps to empty
    [InlineData(1.5f, 1f)] // above range clamps to full
    public void Pack_ClampsOutOfRangeValues(float input, float expected)
    {
        Assert.Equal(expected, RoundTrip(input, 0f, 0f, 0f).Warmth, 0.01);
    }

    [Fact]
    public void RoundTrip_PreservesTheElevatedThreshold()
    {
        // The panel colours a drive, and relax acts on it, at exactly this value. Quantisation
        // must not drop a drive that had reached it back below.
        var d = RoundTrip(VillagerDrives.ElevatedThreshold, 0f, 0f, 0f);
        Assert.True(d.Warmth >= VillagerDrives.ElevatedThreshold,
            $"{d.Warmth} fell below the {VillagerDrives.ElevatedThreshold} threshold");
    }
}
