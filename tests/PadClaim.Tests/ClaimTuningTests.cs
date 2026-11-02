namespace PadClaim.Tests;

/// <summary>Pins the thresholds table the claim state machine is designed around.</summary>
public sealed class ClaimTuningTests
{
    /// <summary>Every field of the default tuning matches the plan's thresholds table.</summary>
    [Fact]
    public void DefaultsMatchThePlan()
    {
        ClaimTuning tuning = ClaimTuning.Default;

        Assert.Equal(TimeSpan.FromSeconds(1.0), tuning.QuietWindow);
        Assert.Equal(TimeSpan.FromSeconds(10), tuning.SettleLimit);
        Assert.Equal(0.05f, tuning.AxisStillBand);
        Assert.Equal(TimeSpan.FromMilliseconds(50), tuning.MinClaimHold);
        Assert.Equal(TimeSpan.FromSeconds(2.0), tuning.MaxClaimHold);
        Assert.Equal(TimeSpan.FromMilliseconds(10), tuning.SubHumanHold);
        Assert.Equal(TimeSpan.FromSeconds(10), tuning.StuckHold);
        Assert.Equal(TimeSpan.FromSeconds(3.0), tuning.ConfirmWindow);
        Assert.Equal(5, tuning.RepeatPresses);
        Assert.Equal(TimeSpan.FromMilliseconds(20), tuning.RepeatJitterFloor);
        Assert.Equal(0.04f, tuning.RepeatJitterFraction);
        Assert.Equal(TimeSpan.FromSeconds(60), tuning.RepeatHorizon);
        Assert.Equal(TimeSpan.FromSeconds(2.0), tuning.SeatStealIdle);
    }
}
