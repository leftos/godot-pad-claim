using PadClaim.Tests.Support;

namespace PadClaim.Tests;

/// <summary>
/// Pins the seat: the first confirmed claim takes it, a second pad takes it only from an idle holder, a reconnecting
/// holder gets it back by itself, the host can close claiming or release the seat, and a button held through the claim
/// never leaks into the game.
/// </summary>
public sealed class SeatTests
{
    private const int A = 0;
    private const int B = 1;
    private const int C = 2;

    private static readonly int[] NoButtons = [];
    private static readonly float[] Centred = [0f, 0f, 0f, 0f, 0f, 0f];
    private static readonly DeviceIdentity OtherPad = new(0x054C, 0x09CC, "Other pad", null);

    /// <summary>Plan case 19: of two pads gesturing at once, the first to confirm takes the seat and the other is refused.</summary>
    [Fact]
    public void TwoPadsPressingAtOnceFirstConfirmWins()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(Hold(TwoPads(), 0, A, 1500, 1600), 0, B, 1750, 1900);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(script, 1, A, 1550, 1650), 1, B, 1700, 1800).Run(claimer);

        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal((Ms(1800), 1), (claimed.At, claimed.Event.Device));
        (TimeSpan At, ClaimRefused Event) refused = Assert.Single(Of<ClaimRefused>(events));
        Assert.Equal((Ms(1900), 0, RefuseReason.SeatBusy), (refused.At, refused.Event.Device, refused.Event.Reason));
        Assert.Equal(1, claimer.SeatDevice);
    }

    /// <summary>Plan case 20: a pad completing the gesture takes the seat from a holder idle for the steal time.</summary>
    [Fact]
    public void SeatMovesWhenHolderIdle()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = PadOneClaimsThenPadZeroGestures(TwoPads()).Run(claimer);

        (TimeSpan At, SeatMoved Event) moved = Assert.Single(Of<SeatMoved>(events));
        Assert.Equal((Ms(4400), 1, 0), (moved.At, moved.Event.From, moved.Event.To));
        Assert.Empty(Of<SeatLost>(events));
        Assert.Equal(0, claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 1));
        Assert.Equal(DeviceStatus.Claimed, StatusOf(claimer, 0));
    }

    /// <summary>A holder that pressed a button within the steal time keeps the seat.</summary>
    [Fact]
    public void SeatStaysWhenHolderActive()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = PadOneClaimsThenPadZeroGestures(Hold(TwoPads(), 1, C, 4200, 4250)).Run(claimer);

        (TimeSpan At, ClaimRefused Event) refused = Assert.Single(Of<ClaimRefused>(events));
        Assert.Equal((Ms(4400), 0, RefuseReason.SeatBusy), (refused.At, refused.Event.Device, refused.Event.Reason));
        Assert.Empty(Of<SeatMoved>(events));
        Assert.Equal(1, claimer.SeatDevice);
    }

    /// <summary>Plan case 21: a button down when the claim lands is refused until it is released.</summary>
    [Fact]
    public void ButtonHeldThroughClaimIsRefusedUntilReleased()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(Hold(ConnectedPad(0), 0, A, 1500, 1600).Press(1700, 0, C), 0, B, 1800, 1900).Until(2000);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal((Ms(1900), 0), (claimed.At, claimed.Event.Device));
        Assert.False(claimer.AcceptsButton(0, C));
        Assert.True(claimer.AcceptsButton(0, A));

        new PadScript().Release(2500, 0, C).Until(2600).Run(claimer);

        Assert.True(claimer.AcceptsButton(0, C));
    }

    /// <summary>Plan case 22: the holder reconnecting under a new device index gets the seat back once it settles.</summary>
    [Fact]
    public void ReconnectedClaimedPadGetsSeatBackAfterQuietWindow()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = HolderLeavesAndReturnsAsThree(ConnectedPad(0)).Run(claimer);

        (TimeSpan At, SeatLost Event) lost = Assert.Single(Of<SeatLost>(events));
        Assert.Equal((Ms(3000), 0, SeatLossReason.Disconnected), (lost.At, lost.Event.Device, lost.Event.Reason));
        (TimeSpan At, SeatRestored Event) restored = Assert.Single(Of<SeatRestored>(events));
        Assert.Equal((Ms(4500), 3), (restored.At, restored.Event.Device));
        int readyIndex = IndexOf(events, recorded => recorded.Event is DeviceChanged { Snapshot: { Device: 3, Status: DeviceStatus.Ready } });
        int restoredIndex = IndexOf(events, recorded => recorded.Event is SeatRestored);
        Assert.Equal(Ms(4500), events[readyIndex].At);
        Assert.True(readyIndex < restoredIndex);
        Assert.Equal(3, claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Claimed, StatusOf(claimer, 3));
    }

    /// <summary>Plan case 23: when two connected pads match the holder's identity, neither gets the seat back.</summary>
    [Fact]
    public void ReconnectWithAmbiguousIdentityDoesNotRestore()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        Assert.True(PadScript.IdentityOf(0).Matches(PadScript.IdentityOf(1)));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = HolderLeavesAndReturnsAsThree(TwoPads()).Run(claimer);

        Assert.Single(Of<SeatLost>(events));
        Assert.Empty(Of<SeatRestored>(events));
        Assert.Null(claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 3));
    }

    /// <summary>A pad that takes the vacant seat while the holder is away keeps it when the holder returns.</summary>
    [Fact]
    public void SeatTakenWhileHolderAwayIsNotRestored()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        claimer.Connect(1, OtherPad, true, NoButtons, Centred, Ms(0));
        PadScript script = Hold(Hold(HolderLeavesAndReturnsAsThree(ConnectedPad(0)), 1, A, 3100, 3200), 1, B, 3300, 3400);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events), recorded => recorded.Event.Device == 1);
        Assert.Equal(Ms(3400), claimed.At);
        Assert.Empty(Of<SeatRestored>(events));
        Assert.Equal(1, claimer.SeatDevice);
    }

    /// <summary>A complete gesture while the host has closed claiming is refused and leaves the seat vacant.</summary>
    [Fact]
    public void ClosedClaimsAreRefused()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty) { ClaimsOpen = false };

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(ConnectedPad(0)).Run(claimer);

        (TimeSpan At, ClaimRefused Event) refused = Assert.Single(Of<ClaimRefused>(events));
        Assert.Equal((Ms(2100), 0, RefuseReason.ClaimsClosed), (refused.At, refused.Event.Device, refused.Event.Reason));
        Assert.Empty(Of<SeatClaimed>(events));
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>The host releasing the seat vacates it, and the former holder's identity is not given it back.</summary>
    [Fact]
    public void ReleaseSeatVacatesIt()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        CleanClaim(ConnectedPad(0)).Until(2500).Run(claimer);

        IReadOnlyList<ClaimEvent> released = claimer.ReleaseSeat(Ms(2500));

        SeatLost lost = Assert.IsType<SeatLost>(Assert.Single(released));
        Assert.Equal((0, SeatLossReason.Released), (lost.Device, lost.Reason));
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 0));
        Assert.Null(claimer.SeatDevice);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> later = new PadScript()
            .Disconnect(3000, 0)
            .Connect(3500, 3, NoButtons, Centred)
            .Until(4600)
            .Run(claimer);

        Assert.Empty(Of<SeatRestored>(later));
        Assert.Null(claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 3));
    }

    /// <summary>Releasing a vacant seat forgets a holder that disconnected, so its return is not given the seat.</summary>
    [Fact]
    public void ReleaseSeatForgetsADisconnectedHolder()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        CleanClaim(ConnectedPad(0)).Disconnect(3000, 0).Until(3200).Run(claimer);

        Assert.Empty(claimer.ReleaseSeat(Ms(3200)));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> later = new PadScript().Connect(3500, 3, NoButtons, Centred).Until(4600).Run(claimer);

        Assert.Empty(Of<SeatRestored>(later));
        Assert.Null(claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 3));
    }

    /// <summary>Only the seat device's buttons and axes reach the game.</summary>
    [Fact]
    public void OnlyTheSeatDeviceIsAccepted()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        CleanClaim(TwoPads()).Run(claimer);

        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 1));
        Assert.False(claimer.AcceptsButton(1, A));
        Assert.False(claimer.AcceptsAxis(1, 0));
        Assert.True(claimer.AcceptsAxis(0, 0));
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static PadScript ConnectedPad(int device) => new PadScript().Connect(0, device, NoButtons, Centred);

    private static PadScript TwoPads() => ConnectedPad(0).Connect(0, 1, NoButtons, Centred);

    private static PadScript Hold(PadScript script, int device, int button, int down, int up) =>
        script.Press(down, device, button).Release(up, device, button);

    private static PadScript CleanClaim(PadScript script) => Hold(Hold(script, 0, A, 1500, 1600), 0, B, 2000, 2100);

    private static PadScript PadOneClaimsThenPadZeroGestures(PadScript script) =>
        Hold(Hold(Hold(Hold(script, 1, A, 1500, 1600), 1, B, 1700, 1800), 0, A, 4000, 4100), 0, B, 4300, 4400);

    private static PadScript HolderLeavesAndReturnsAsThree(PadScript script) =>
        CleanClaim(script).Disconnect(3000, 0).Connect(3500, 3, NoButtons, Centred).Until(4600);

    private static int IndexOf(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events, Func<(TimeSpan At, ClaimEvent Event), bool> match)
    {
        for (int index = 0; index < events.Count; index++)
        {
            if (match(events[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static List<(TimeSpan At, T Event)> Of<T>(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events)
        where T : ClaimEvent => [.. events.Where(recorded => recorded.Event is T).Select(recorded => (recorded.At, (T)recorded.Event))];

    private static DeviceStatus StatusOf(PadClaimer claimer, int device) => claimer.Devices.Single(snapshot => snapshot.Device == device).Status;
}
