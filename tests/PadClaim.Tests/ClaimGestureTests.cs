using PadClaim.Tests.Support;

namespace PadClaim.Tests;

/// <summary>
/// Pins the deliberate two-button gesture: a pad that has stood still for the quiet window claims by pressing and
/// releasing one button with a human hold, then pressing and releasing a different one within the confirm window.
/// </summary>
public sealed class ClaimGestureTests
{
    private const int A = 0;
    private const int B = 1;

    private static readonly int[] NoButtons = [];
    private static readonly float[] Centred = [0f, 0f, 0f, 0f, 0f, 0f];

    /// <summary>Plan case 1: a still pad claims on the second button's release, not on its press.</summary>
    [Fact]
    public void CleanPadClaimsOnReleaseThenSecondButton()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(ConnectedPad(0)).Run(claimer);

        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(1600), 0, A), (started.At, started.Event.Device, started.Event.FirstButton));
        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal((Ms(2100), 0), (claimed.At, claimed.Event.Device));
        Assert.DoesNotContain(events, recorded => recorded.At == Ms(2000));
        Assert.Equal(0, claimer.SeatDevice);
    }

    /// <summary>Plan case 2: a button pressed and never released starts nothing.</summary>
    [Fact]
    public void PressWithoutReleaseNeverClaims()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(ConnectedPad(0).Press(1500, 0, A), 0, B, 1700, 1800).Run(claimer);

        Assert.Empty(Of<SeatClaimed>(events));
    }

    /// <summary>Plan case 4: a button down at connect and released while settling only restarts the quiet window.</summary>
    [Fact]
    public void ButtonReleasedWhileSettlingDelaysReadyOnly()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        PadScript script = new PadScript().Connect(0, 0, [A], Centred).Release(400, 0, A).Until(1500);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1400), DeviceStatus.Ready), (changed.At, changed.Event.Snapshot.Status));
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 0));
    }

    /// <summary>Plan case 7: a hold longer than a human press is not a first button; the next good release is.</summary>
    [Fact]
    public void HoldPastHumanHoldTimeDoesNotClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(ConnectedPad(0), 0, A, 1500, 4000), 0, B, 4200, 4300).Run(claimer);

        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(4300), B), (started.At, started.Event.FirstButton));
        Assert.Equal(DeviceStatus.AwaitingConfirm, StatusOf(claimer, 0));
    }

    /// <summary>Plan case 9: a tap shorter than the minimum hold is not a first button.</summary>
    [Fact]
    public void TapShorterThanMinHoldDoesNotClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(ConnectedPad(0), 0, A, 1500, 1530), 0, B, 1700, 1800).Run(claimer);

        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(1800), B), (started.At, started.Event.FirstButton));
    }

    /// <summary>Plan case 10: the first button pressed again re-arms the claim and never confirms it.</summary>
    [Fact]
    public void ConfirmWithSameButtonDoesNotClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        PadScript script = Hold(Hold(ConnectedPad(0), 0, A, 1500, 1600), 0, A, 1800, 1900);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(script, 0, B, 2100, 2200).Run(claimer);

        Assert.Equal([(Ms(1600), A), (Ms(1900), A)], Of<ClaimStarted>(events).Select(started => (started.At, started.Event.FirstButton)));
        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal(Ms(2200), claimed.At);
    }

    /// <summary>Plan case 11: a second button pressed after the confirm window becomes the new first button.</summary>
    [Fact]
    public void ConfirmAfterWindowDoesNotClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(ConnectedPad(0), 0, A, 1500, 1600), 0, B, 4700, 4800).Run(claimer);

        (TimeSpan At, ClaimCancelled Event) cancelled = Assert.Single(Of<ClaimCancelled>(events));
        Assert.Equal((Ms(4610), 0, CancelReason.Timeout), (cancelled.At, cancelled.Event.Device, cancelled.Event.Reason));
        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events), recorded => recorded.Event.FirstButton == B);
        Assert.Equal(Ms(4800), started.At);
        Assert.Empty(Of<SeatClaimed>(events));
    }

    /// <summary>Plan case 12: an axis resting far off centre is still, so the pad settles, records the rest and claims.</summary>
    [Fact]
    public void AxisRestingOffCentreStaysClaimableAndRecordsCentre()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        float[] throttleBack = [0f, -0.98f, 0f, 0f, 0f, 0f];
        PadScript script = new PadScript().Connect(0, 0, NoButtons, throttleBack).Axis(10, 0, 1, -0.98f);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script).Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1000), DeviceStatus.Ready), (changed.At, changed.Event.Snapshot.Status));
        Assert.Equal(new AxisRest(-0.98f, 0f, true), claimer.RestOf(0, 1));
        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal(Ms(2100), claimed.At);
    }

    /// <summary>Plan case 14: axes swinging end to end on a ready pad never start a claim.</summary>
    [Fact]
    public void AxisSwingNeverClaims()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = ConnectedPad(0).Axis(1500, 0, 0, -1f).Axis(1600, 0, 0, 1f).Axis(1700, 0, 0, 0f).Axis(1800, 0, 5, 1f).Axis(1900, 0, 5, 0f);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal(Ms(1000), changed.At);
        Assert.DoesNotContain(events, recorded => recorded.At > Ms(1000));
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 0));
    }

    /// <summary>Plan case 29: a pad that disconnects with a claim started loses it, and reconnects settling.</summary>
    [Fact]
    public void DisconnectMidClaimCancelsCandidate()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), 0, A, 1500, 1600).Disconnect(1700, 0).Connect(1800, 0, NoButtons, Centred);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(script, 0, B, 1900, 2000).Run(claimer);

        var atDisconnect = events.Where(recorded => recorded.At == Ms(1700)).Select(recorded => recorded.Event).ToList();
        Assert.Equal(2, atDisconnect.Count);
        ClaimCancelled cancelled = Assert.IsType<ClaimCancelled>(atDisconnect[0]);
        Assert.Equal((0, CancelReason.Disconnected), (cancelled.Device, cancelled.Reason));
        Assert.Equal(0, Assert.IsType<DeviceRemoved>(atDisconnect[1]).Device);
        Assert.Empty(Of<SeatClaimed>(events));
        Assert.Equal(DeviceStatus.Settling, StatusOf(claimer, 0));
    }

    /// <summary>Plan case 30: the seat holder disconnecting loses the seat and leaves it vacant.</summary>
    [Fact]
    public void ClaimedPadDisconnectVacatesSeat()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(ConnectedPad(0)).Disconnect(2500, 0).Run(claimer);

        var atDisconnect = events.Where(recorded => recorded.At == Ms(2500)).Select(recorded => recorded.Event).ToList();
        Assert.Equal(2, atDisconnect.Count);
        SeatLost lost = Assert.IsType<SeatLost>(atDisconnect[0]);
        Assert.Equal((0, SeatLossReason.Disconnected), (lost.Device, lost.Reason));
        Assert.Equal(0, Assert.IsType<DeviceRemoved>(atDisconnect[1]).Device);
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>Plan case 34: a gesture made while settling restarts the quiet window and claims nothing.</summary>
    [Fact]
    public void GestureWhileSettlingDoesNotClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(ConnectedPad(0), 0, A, 100, 200), 0, B, 400, 500).Until(1600).Run(claimer);

        Assert.Empty(Of<ClaimStarted>(events));
        Assert.Empty(Of<SeatClaimed>(events));
        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1500), DeviceStatus.Ready), (changed.At, changed.Event.Snapshot.Status));
    }

    /// <summary>A second pad's complete gesture is refused while the first pad holds the seat.</summary>
    [Fact]
    public void SecondPadIsRefusedWhileSeatHeld()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = CleanClaim(ConnectedPad(0).Connect(0, 1, NoButtons, Centred));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(script, 1, A, 2500, 2600), 1, B, 2700, 2800).Run(claimer);

        (TimeSpan At, ClaimRefused Event) refused = Assert.Single(Of<ClaimRefused>(events));
        Assert.Equal((Ms(2800), 1, RefuseReason.SeatBusy), (refused.At, refused.Event.Device, refused.Event.Reason));
        Assert.Equal(0, claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 1));
    }

    /// <summary>A joypad the engine has no layout for is listed as not a gamepad and cannot claim.</summary>
    [Fact]
    public void UnknownJoypadIsNotAGamepad()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = new PadScript().Connect(0, 0, NoButtons, Centred, isKnownGamepad: false);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script).Run(claimer);

        (TimeSpan At, ClaimEvent Event) only = Assert.Single(events);
        DeviceAdded added = Assert.IsType<DeviceAdded>(only.Event);
        Assert.Equal((0, DeviceStatus.NotAGamepad), (added.Snapshot.Device, added.Snapshot.Status));
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>A sample from a device that never connected changes nothing by default.</summary>
    [Fact]
    public void EventFromUnconnectedDeviceIsIgnored()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        Assert.Empty(claimer.Button(7, A, true, Ms(0)));
        Assert.Empty(claimer.Button(7, A, false, Ms(100)));
        Assert.Empty(claimer.Axis(7, 0, 1f, Ms(150)));
        Assert.Empty(claimer.Tick(Ms(200)));
        Assert.Empty(claimer.Devices);
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static PadScript ConnectedPad(int device) => new PadScript().Connect(0, device, NoButtons, Centred);

    private static PadScript Hold(PadScript script, int device, int button, int down, int up) =>
        script.Press(down, device, button).Release(up, device, button);

    private static PadScript CleanClaim(PadScript script) => Hold(Hold(script, 0, A, 1500, 1600), 0, B, 2000, 2100);

    private static List<(TimeSpan At, T Event)> Of<T>(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events)
        where T : ClaimEvent => [.. events.Where(recorded => recorded.Event is T).Select(recorded => (recorded.At, (T)recorded.Event))];

    private static DeviceStatus StatusOf(PadClaimer claimer, int device) => claimer.Devices.Single(snapshot => snapshot.Device == device).Status;
}
