using PadClaim.Tests.Support;

namespace PadClaim.Tests;

/// <summary>
/// Pins noise detection: a pad that sends input on its own (a stuck button, a self-repeating button, a sub-human toggle,
/// an axis that never settles) becomes suspect and can never start or confirm a claim, while a player pressing buttons
/// on their own claimed pad is never quarantined.
/// </summary>
public sealed class NoiseTests
{
    private const int A = 0;
    private const int B = 1;
    private const int C = 2;
    private const int D = 3;
    private const int HeldButton = 5;

    private static readonly int[] NoButtons = [];
    private static readonly float[] Centred = [0f, 0f, 0f, 0f, 0f, 0f];

    /// <summary>Plan case 3: a button down at connect and still down when the quiet window ends marks the pad suspect.</summary>
    [Fact]
    public void ButtonHeldAtConnectMarksDeviceSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = new PadScript().Connect(0, 0, [HeldButton], Centred);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script).Run(claimer);

        AssertSuspect(Assert.Single(Of<DeviceChanged>(events)), Ms(1000), SuspectReason.StuckButton, [HeldButton]);
        Assert.Empty(Of<ClaimStarted>(events));
        Assert.Empty(Of<SeatClaimed>(events));
    }

    /// <summary>Plan case 5a: a button pressing itself every 300 ms is suspect on its fifth press, before the pad settles.</summary>
    [Fact]
    public void FastSelfRepeatingButtonIsSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Presses(ConnectedPad(0), C, 100, 0, 300, 600, 900, 1200).Run(claimer);

        AssertSuspect(Assert.Single(Of<DeviceChanged>(events)), Ms(1200), SuspectReason.RepeatingButton, [C]);
    }

    /// <summary>Plan case 5b: a button pressing itself every 3 s with human holds never claims and is suspect on its fifth press.</summary>
    [Fact]
    public void SlowSelfRepeatingButtonIsSuspectAfterFivePresses()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Presses(ConnectedPad(0), C, 120, 2000, 5000, 8000, 11000, 14000).Run(claimer);

        Assert.Empty(Of<SeatClaimed>(events));
        List<(TimeSpan At, DeviceChanged Event)> changes = Of<DeviceChanged>(events);
        Assert.Equal(2, changes.Count);
        AssertSuspect(changes[1], Ms(14000), SuspectReason.RepeatingButton, [C]);
    }

    /// <summary>Plan case 6: a release 2 ms after its press is faster than a finger and marks the pad suspect.</summary>
    [Fact]
    public void SubHumanToggleIsSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(ConnectedPad(0), D, 1500, 1502).Run(claimer);

        AssertSuspect(Of<DeviceChanged>(events)[^1], Ms(1502), SuspectReason.SubHumanToggle, [D]);
    }

    /// <summary>Plan case 8: a button held for the stuck limit on a ready pad marks the pad suspect at the first tick past it.</summary>
    [Fact]
    public void HoldPastStuckHoldMarksSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0).Press(1500, 0, A).Until(11600).Run(claimer);

        List<(TimeSpan At, DeviceChanged Event)> changes = Of<DeviceChanged>(events);
        Assert.Equal(2, changes.Count);
        AssertSuspect(changes[1], Ms(11500), SuspectReason.StuckButton, [A]);
    }

    /// <summary>Plan case 13: an axis that keeps leaving the still band never settles, and the pad is suspect at the settle limit.</summary>
    [Fact]
    public void JitteryAxisNeverSettlesAndIsSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = ConnectedPad(0);
        for (int t = 50; t <= 10000; t += 50)
        {
            script.Axis(t, 0, 0, t / 50 % 2 == 1 ? 0.08f : -0.08f);
        }

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        AssertSuspect(Assert.Single(Of<DeviceChanged>(events)), Ms(10000), SuspectReason.NeverSettled | SuspectReason.JitteryAxis, NoButtons);
    }

    /// <summary>Plan case 15: a ready pad whose button starts repeating loses its started claim and ignores a later gesture.</summary>
    [Fact]
    public void DeviceNoisyAfterQuietWindowIsQuarantined()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Presses(ConnectedPad(0), C, 100, 5000, 5400, 5800, 6200, 6600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(script, A, 7000, 7100), B, 7300, 7400).Run(claimer);

        AssertSuspect(Of<DeviceChanged>(events)[^1], Ms(6600), SuspectReason.RepeatingButton, [C]);
        (TimeSpan At, ClaimCancelled Event) cancelled = Assert.Single(Of<ClaimCancelled>(events));
        Assert.Equal((Ms(6600), CancelReason.DeviceSuspect), (cancelled.At, cancelled.Event.Reason));
        Assert.DoesNotContain(Of<ClaimStarted>(events), started => started.At > Ms(6600));
        Assert.Empty(Of<SeatClaimed>(events));
    }

    /// <summary>Plan case 16: a sub-human toggle while a claim waits for its confirm cancels the claim.</summary>
    [Fact]
    public void NoisyDeviceMidClaimCancelsClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(ConnectedPad(0), A, 1500, 1600), D, 1700, 1702).Run(claimer);

        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(1600), A), (started.At, started.Event.FirstButton));
        (TimeSpan At, ClaimCancelled Event) cancelled = Assert.Single(Of<ClaimCancelled>(events));
        Assert.Equal((Ms(1702), CancelReason.DeviceSuspect), (cancelled.At, cancelled.Event.Reason));
        AssertSuspect(Of<DeviceChanged>(events)[^1], Ms(1702), SuspectReason.SubHumanToggle, [D]);
    }

    /// <summary>Plan case 17: presses at a hand's uneven rhythm are not a self-repeating button.</summary>
    [Fact]
    public void IrregularHumanPressesAreNotRepeating()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Presses(ConnectedPad(0), A, 80, 1500, 1700, 2120, 2300, 2640, 2900, 3320, 3500);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.DoesNotContain(Of<DeviceChanged>(events), changed => changed.Event.Snapshot.Status == DeviceStatus.Suspect);
        Assert.NotEqual(DeviceStatus.Suspect, StatusOf(claimer, 0));
    }

    /// <summary>Plan case 18: the seat holder holding, mashing and tapping its buttons stays claimed.</summary>
    [Fact]
    public void ClaimedPadIsNeverQuarantined()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = CleanClaim(ConnectedPad(0)).Press(3000, 0, A).Release(18000, 0, A);
        script = Presses(script, C, 100, [.. Enumerable.Range(0, 11).Select(index => 3000 + (300 * index))]);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(script, D, 6500, 6502).Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1000), DeviceStatus.Ready), (changed.At, changed.Event.Snapshot.Status));
        Assert.Empty(Of<ClaimCancelled>(events));
        Assert.Equal(DeviceStatus.Claimed, StatusOf(claimer, 0));
        Assert.Equal(0, claimer.SeatDevice);
    }

    /// <summary>A suspect pad's clean gesture neither starts nor confirms a claim.</summary>
    [Fact]
    public void SuspectDeviceCannotClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), D, 1500, 1502);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(script, A, 2000, 2100), B, 2300, 2400).Run(claimer);

        Assert.DoesNotContain(Of<ClaimStarted>(events), started => started.At > Ms(1502));
        Assert.Empty(Of<SeatClaimed>(events));
        Assert.Equal(DeviceStatus.Suspect, StatusOf(claimer, 0));
    }

    /// <summary>A button down at connect and released at once only delays Ready: its true press time is unknown.</summary>
    [Fact]
    public void ButtonDownAtConnectReleasedAtOnceIsNotSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = new PadScript().Connect(0, 0, [A], Centred).Release(5, 0, A).Until(1005).Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1005), DeviceStatus.Ready), (changed.At, changed.Event.Snapshot.Status));
    }

    /// <summary>Presses whose intervals sit exactly at the jitter bound (4% of a 1000 ms mean) are periodic.</summary>
    [Fact]
    public void RepeatAtTheJitterBoundIsSuspect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Presses(ConnectedPad(0), C, 100, 2000, 3040, 4000, 5000, 6000).Run(claimer);

        AssertSuspect(Of<DeviceChanged>(events)[^1], Ms(6000), SuspectReason.RepeatingButton, [C]);
    }

    /// <summary>A hold of exactly the sub-human limit is human.</summary>
    [Fact]
    public void HoldOfTenMillisecondsIsNotSubHuman()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(ConnectedPad(0), D, 1500, 1510).Run(claimer);

        Assert.DoesNotContain(Of<DeviceChanged>(events), changed => changed.Event.Snapshot.Status == DeviceStatus.Suspect);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 0));
    }

    /// <summary>A hold just under the sub-human limit marks the pad suspect.</summary>
    [Fact]
    public void HoldUnderTenMillisecondsIsSubHuman()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(ConnectedPad(0), D, 1500, 1509).Run(claimer);

        AssertSuspect(Of<DeviceChanged>(events)[^1], Ms(1509), SuspectReason.SubHumanToggle, [D]);
    }

    private static void AssertSuspect((TimeSpan At, DeviceChanged Event) changed, TimeSpan at, SuspectReason reasons, int[] buttons)
    {
        DeviceSnapshot snapshot = changed.Event.Snapshot;
        Assert.Equal((at, DeviceStatus.Suspect, reasons), (changed.At, snapshot.Status, snapshot.Reasons));
        Assert.Equal(buttons, snapshot.SuspectButtons);
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static PadScript ConnectedPad(int device) => new PadScript().Connect(0, device, NoButtons, Centred);

    private static PadScript Hold(PadScript script, int button, int down, int up) => script.Press(down, 0, button).Release(up, 0, button);

    private static PadScript Presses(PadScript script, int button, int holdMilliseconds, params int[] downs)
    {
        foreach (int down in downs)
        {
            Hold(script, button, down, down + holdMilliseconds);
        }

        return script;
    }

    private static PadScript CleanClaim(PadScript script) => Hold(Hold(script, A, 1500, 1600), B, 2000, 2100);

    private static List<(TimeSpan At, T Event)> Of<T>(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events)
        where T : ClaimEvent => [.. events.Where(recorded => recorded.Event is T).Select(recorded => (recorded.At, (T)recorded.Event))];

    private static DeviceStatus StatusOf(PadClaimer claimer, int device) => claimer.Devices.Single(snapshot => snapshot.Device == device).Status;
}
