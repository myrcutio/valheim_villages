using ValheimVillages.TaskQueue.Handlers;
using Xunit;

namespace ValheimVillages.Tests.Navigation;

public class CellValidatorTests
{
    // ── Case 2: Slope ─────────────────────────────────────────────────

    private const float CellSize = 3f;
    // ── Case 1: Ledge drop ────────────────────────────────────────────

    [Fact]
    public void IsLedgeDrop_FlatGround_ReturnsFalse()
    {
        Assert.False(CellValidator.IsLedgeDrop(10f, 10f));
    }

    [Fact]
    public void IsLedgeDrop_ClimbingUp_ReturnsFalse()
    {
        Assert.False(CellValidator.IsLedgeDrop(10f, 15f));
    }

    [Fact]
    public void IsLedgeDrop_SmallDrop_ReturnsFalse()
    {
        Assert.False(CellValidator.IsLedgeDrop(10f, 9.0f));
    }

    [Fact]
    public void IsLedgeDrop_ExactThreshold_ReturnsFalse()
    {
        // MaxLedgeDrop = 1.5; check is strictly greater-than
        Assert.False(CellValidator.IsLedgeDrop(10f, 8.5f));
    }

    [Fact]
    public void IsLedgeDrop_JustOverThreshold_ReturnsTrue()
    {
        Assert.True(CellValidator.IsLedgeDrop(10f, 8.49f));
    }

    [Fact]
    public void IsLedgeDrop_LargeDrop_ReturnsTrue()
    {
        Assert.True(CellValidator.IsLedgeDrop(40f, 37f));
    }

    [Fact]
    public void IsTooSteep_FlatGround_ReturnsFalse()
    {
        Assert.False(CellValidator.IsTooSteep(0, 0, 0, CellSize, 0, 0));
    }

    [Fact]
    public void IsTooSteep_GentleSlope15Deg_ReturnsFalse()
    {
        var dy = CellSize * (float)Math.Tan(15.0 * Math.PI / 180.0);
        Assert.False(CellValidator.IsTooSteep(0, 0, 0, CellSize, dy, 0));
    }

    [Fact]
    public void IsTooSteep_JustBelowThreshold_ReturnsFalse()
    {
        // 29.9° is safely under the 30° threshold even with float rounding
        var dy = CellSize * (float)Math.Tan(29.9 * Math.PI / 180.0);
        Assert.False(CellValidator.IsTooSteep(0, 0, 0, CellSize, dy, 0));
    }

    [Fact]
    public void IsTooSteep_JustOverThreshold_ReturnsTrue()
    {
        var dy = CellSize * (float)Math.Tan(31.0 * Math.PI / 180.0);
        Assert.True(CellValidator.IsTooSteep(0, 0, 0, CellSize, dy, 0));
    }

    [Fact]
    public void IsTooSteep_SteepSlope45Deg_ReturnsTrue()
    {
        Assert.True(CellValidator.IsTooSteep(0, 0, 0, CellSize, CellSize, 0));
    }

    [Fact]
    public void IsTooSteep_DownhillSameAngle_MatchesUphill()
    {
        var dy = CellSize * (float)Math.Tan(31.0 * Math.PI / 180.0);
        // Going downhill: fromY higher than toY -- same absolute angle
        Assert.True(CellValidator.IsTooSteep(0, dy, 0, CellSize, 0, 0));
    }

    [Fact]
    public void IsTooSteep_VerticalWithinClimb_ReturnsFalse()
    {
        // Near-zero horizontal distance, dy within agent step height (0.3m)
        Assert.False(CellValidator.IsTooSteep(0, 0, 0, 0.005f, 0.2f, 0));
    }

    [Fact]
    public void IsTooSteep_VerticalExceedingClimb_ReturnsTrue()
    {
        // Near-zero horizontal distance, dy exceeds agent step height (0.3m)
        Assert.True(CellValidator.IsTooSteep(0, 0, 0, 0.005f, 0.5f, 0));
    }

    [Fact]
    public void IsTooSteep_DiagonalCell_UsesFullHorizontalDistance()
    {
        // Diagonal neighbor: dx=3, dz=3 → horiz=√18≈4.24m
        // A height that's steep for 3m but gentle for 4.24m
        var horiz = (float)Math.Sqrt(CellSize * CellSize + CellSize * CellSize);
        var dy = horiz * (float)Math.Tan(29.0 * Math.PI / 180.0);
        Assert.False(CellValidator.IsTooSteep(0, 0, 0, CellSize, dy, CellSize));
    }
}