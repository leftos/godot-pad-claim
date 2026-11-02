using PadClaim.Tests.Support;

namespace PadClaim.Tests;

/// <summary>
/// Pins the player's per-device decisions and the settings-screen operations: blocking a pad or a button, trusting a
/// pad, forgetting a pad, re-sampling a suspect pad, forcing a claim, and admitting pads never seen to connect.
/// </summary>
public sealed class SettingsTests
{
    private const int A = 0;
    private const int B = 1;
    private const int C = 2;
    private const int D = 3;
    private const int BlockedButton = 7;
    private const int UnlistedPad = 7;

    private static readonly int[] NoButtons = [];
    private static readonly float[] Centred = [0f, 0f, 0f, 0f, 0f, 0f];
    private static readonly DeviceIdentity Pad0 = PadScript.IdentityOf(0);
    private static readonly DeviceIdentity OtherPad = new(0x054C, 0x09CC, "Other pad", null);

    /// <summary>Plan case 24: a pad whose identity is blocked connects blocked and its gesture is ignored.</summary>
    [Fact]
    public void BlocklistedDeviceIsNeverClaimable()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.Blocked));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(ConnectedPad(0), 0, 1500).Run(claimer);

        Assert.Equal(DeviceStatus.Blocked, Assert.Single(Of<DeviceAdded>(events)).Event.Snapshot.Status);
        Assert.Empty(Of<ClaimStarted>(events));
        Assert.Empty(Of<SeatClaimed>(events));
    }

    /// <summary>Plan case 25: a blocked button down at connect neither delays nor suspects the pad, and never claims.</summary>
    [Fact]
    public void BlocklistedButtonIsIgnoredAndCannotMakeDeviceSuspect()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.None, BlockedButton));
        PadScript script = new PadScript().Connect(0, 0, [BlockedButton], Centred);
        Hold(script, 0, BlockedButton, 1500, 1600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script, 0, 2000).Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1000), DeviceStatus.Ready), (changed.At, changed.Event.Snapshot.Status));
        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(2100), A), (started.At, started.Event.FirstButton));
        Assert.Equal(Ms(2600), Assert.Single(Of<SeatClaimed>(events)).At);
        Assert.False(claimer.AcceptsButton(0, BlockedButton));
    }

    /// <summary>Plan case 26: a pad blocked by identity stays blocked when it reconnects under a new device index.</summary>
    [Fact]
    public void ReconnectedDeviceKeepsItsRuleByIdentity()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = ConnectedPad(0)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Blocked, now))
            .Disconnect(2000, 0)
            .Connect(2500, 4, NoButtons, Centred, identity: Pad0);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        List<ClaimEvent> atBlock = At(events, 1500);
        Assert.Equal(2, atBlock.Count);
        Assert.Equal(DeviceStatus.Blocked, Assert.IsType<DeviceChanged>(atBlock[0]).Snapshot.Status);
        Assert.IsType<SettingsChanged>(atBlock[1]);
        DeviceSnapshot added = Of<DeviceAdded>(events).Single(entry => entry.Event.Snapshot.Device == 4).Event.Snapshot;
        Assert.Equal(DeviceStatus.Blocked, added.Status);
    }

    /// <summary>Plan case 27: a suspect pad that reconnects is sampled afresh and becomes ready.</summary>
    [Fact]
    public void ReconnectedDeviceIsResampled()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), 0, D, 1500, 1502).Disconnect(2000, 0).Connect(2500, 0, NoButtons, Centred).Until(3600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, DeviceChanged Event) last = Of<DeviceChanged>(events)[^1];
        Assert.Equal((Ms(3500), DeviceStatus.Ready), (last.At, last.Event.Snapshot.Status));
    }

    /// <summary>Plan case 28: two serial-less pads with one identity share one rule, and blocking it blocks both.</summary>
    [Fact]
    public void TwoIdenticalSerialLessDevicesShareOneRule()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = ConnectedPad(0)
            .Connect(0, 1, NoButtons, Centred, identity: Pad0)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Blocked, now));

        script.Run(claimer);

        Assert.All(claimer.Devices, snapshot => Assert.Equal(DeviceStatus.Blocked, snapshot.Status));
        Assert.Equal(2, claimer.Devices.Count);
        Assert.Single(claimer.Settings.Rules);
    }

    /// <summary>Plan case 31: a joypad without a gamepad layout is never offered until trusted, then settles afresh.</summary>
    [Fact]
    public void UnknownJoypadIsNotOfferedUntilTrusted()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = new PadScript()
            .Connect(0, 0, NoButtons, Centred, isKnownGamepad: false)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Trusted, now))
            .Until(2600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Equal(DeviceStatus.NotAGamepad, Assert.Single(Of<DeviceAdded>(events)).Event.Snapshot.Status);
        List<ClaimEvent> atTrust = At(events, 1500);
        Assert.Equal(2, atTrust.Count);
        Assert.Equal(DeviceStatus.Settling, Assert.IsType<DeviceChanged>(atTrust[0]).Snapshot.Status);
        Assert.IsType<SettingsChanged>(atTrust[1]);
        (TimeSpan At, DeviceChanged Event) last = Of<DeviceChanged>(events)[^1];
        Assert.Equal((Ms(2500), DeviceStatus.Ready), (last.At, last.Event.Snapshot.Status));
    }

    /// <summary>Plan case 32a: trusting a suspect pad settles it afresh, and it can then claim.</summary>
    [Fact]
    public void TrustedSuspectDeviceCanClaim()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), 0, D, 1500, 1502).Call(2000, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Trusted, now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script, 0, 3500).Run(claimer);

        List<(TimeSpan At, DeviceStatus Status)> changes = [.. Of<DeviceChanged>(events).Select(entry => (entry.At, entry.Event.Snapshot.Status))];
        Assert.Contains((Ms(2000), DeviceStatus.Settling), changes);
        Assert.Contains((Ms(3000), DeviceStatus.Ready), changes);
        Assert.Equal(Ms(4100), Assert.Single(Of<SeatClaimed>(events)).At);
    }

    /// <summary>Plan case 32b: forcing a claim takes a held seat unconditionally, and the former holder is ready.</summary>
    [Fact]
    public void ForceClaimTakesSeat()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = CleanClaim(ConnectedPad(0).Connect(0, 1, NoButtons, Centred), 0, 1500).Call(2200, (pads, now) => pads.ForceClaim(1, now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, SeatMoved Event) moved = Assert.Single(Of<SeatMoved>(events));
        Assert.Equal((Ms(2200), 0, 1), (moved.At, moved.Event.From, moved.Event.To));
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 0));
        Assert.Equal(1, claimer.SeatDevice);
    }

    /// <summary>Forcing a claim on a vacant seat claims it.</summary>
    [Fact]
    public void ForceClaimOnVacantSeatClaims()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0).Call(1500, (pads, now) => pads.ForceClaim(0, now)).Run(claimer);

        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal((Ms(1500), 0), (claimed.At, claimed.Event.Device));
        Assert.Equal(0, claimer.SeatDevice);
    }

    /// <summary>Forcing a claim on a blocked pad does nothing.</summary>
    [Fact]
    public void ForceClaimOnBlockedDeviceDoesNothing()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.Blocked));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0).Call(1500, (pads, now) => pads.ForceClaim(0, now)).Run(claimer);

        Assert.Empty(At(events, 1500));
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>Plan case 33a: input from a pad never seen to connect is ignored by default.</summary>
    [Fact]
    public void UnlistedDeviceIgnoredByDefault()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(new PadScript(), UnlistedPad, A, 100, 200).Run(claimer);

        Assert.Empty(events);
    }

    /// <summary>Plan case 33b: with unlisted pads accepted, a pad never seen to connect is ready at once and can claim.</summary>
    [Fact]
    public void UnlistedDeviceClaimsWhenAccepted()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty, new ClaimOptions(UnlistedDevices: UnlistedDevicePolicy.Accept));

        PadScript script = Hold(Hold(new PadScript(), UnlistedPad, A, 100, 200), UnlistedPad, B, 400, 500);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, DeviceAdded Event) added = Assert.Single(Of<DeviceAdded>(events));
        Assert.Equal(
            (Ms(100), UnlistedPad, DeviceStatus.Ready, true),
            (added.At, added.Event.Snapshot.Device, added.Event.Snapshot.Status, added.Event.Snapshot.Unlisted)
        );
        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(200), UnlistedPad, A), (started.At, started.Event.Device, started.Event.FirstButton));
        (TimeSpan At, SeatClaimed Event) claimed = Assert.Single(Of<SeatClaimed>(events));
        Assert.Equal((Ms(500), UnlistedPad), (claimed.At, claimed.Event.Device));
    }

    /// <summary>A trusted pad with a button down at connect settles ready, not suspect.</summary>
    [Fact]
    public void TrustedPadWithButtonDownSettlesReady()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.Trusted));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = new PadScript().Connect(0, 0, [A], Centred).Until(1100).Run(claimer);

        (TimeSpan At, DeviceChanged Event) changed = Assert.Single(Of<DeviceChanged>(events));
        Assert.Equal((Ms(1000), DeviceStatus.Ready, true), (changed.At, changed.Event.Snapshot.Status, changed.Event.Snapshot.Trusted));
    }

    /// <summary>A button the player blocks no longer starts a claim; the change comes back as settings to save.</summary>
    [Fact]
    public void BlockingAButtonStopsItClaiming()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = ConnectedPad(0).Call(1200, (pads, now) => pads.SetButtonBlocked(Pad0, A, blocked: true, now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script, 0, 1500).Run(claimer);

        Assert.Equal(Ms(1200), Assert.Single(Of<SettingsChanged>(events)).At);
        (TimeSpan At, ClaimStarted Event) started = Assert.Single(Of<ClaimStarted>(events));
        Assert.Equal((Ms(2100), B), (started.At, started.Event.FirstButton));
    }

    /// <summary>Forgetting a blocked pad removes its rule and settles it afresh.</summary>
    [Fact]
    public void ForgetUnblocksAndClearsTheRule()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.Blocked));
        PadScript script = ConnectedPad(0).Call(1500, (pads, now) => pads.Forget(Pad0, now)).Until(2600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        List<ClaimEvent> atForget = At(events, 1500);
        Assert.Equal(2, atForget.Count);
        Assert.Equal(DeviceStatus.Settling, Assert.IsType<DeviceChanged>(atForget[0]).Snapshot.Status);
        Assert.IsType<SettingsChanged>(atForget[1]);
        Assert.Empty(claimer.Settings.Rules);
        (TimeSpan At, DeviceChanged Event) last = Of<DeviceChanged>(events)[^1];
        Assert.Equal((Ms(2500), DeviceStatus.Ready), (last.At, last.Event.Snapshot.Status));
    }

    /// <summary>Re-sampling a suspect pad clears its suspicion and settles it afresh.</summary>
    [Fact]
    public void ResampleClearsSuspicion()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), 0, D, 1500, 1502).Call(2000, (pads, now) => pads.Resample(0, now)).Until(3100);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        DeviceSnapshot resampled = Assert.IsType<DeviceChanged>(Assert.Single(At(events, 2000))).Snapshot;
        Assert.Equal((DeviceStatus.Settling, SuspectReason.None), (resampled.Status, resampled.Reasons));
        Assert.Empty(resampled.SuspectButtons);
        (TimeSpan At, DeviceChanged Event) last = Of<DeviceChanged>(events)[^1];
        Assert.Equal((Ms(3000), DeviceStatus.Ready), (last.At, last.Event.Snapshot.Status));
    }

    /// <summary>A setting that changes nothing raises nothing and adds no rule.</summary>
    [Fact]
    public void UnchangedSettingIsSilent()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.None, now))
            .Run(claimer);

        Assert.Empty(At(events, 1500));
        Assert.Empty(claimer.Settings.Rules);
    }

    /// <summary>The settings change carries the new rules for the host to save.</summary>
    [Fact]
    public void SettingsChangedCarriesTheNewRules()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Blocked, now))
            .Run(claimer);

        DeviceRule rule = Assert.Single(Assert.Single(Of<SettingsChanged>(events)).Event.Settings.Rules);
        Assert.Equal((Pad0, DeviceVerdict.Blocked), (rule.Device, rule.Verdict));
        Assert.Empty(rule.BlockedButtons);
    }

    /// <summary>A pad without a known gamepad layout that is unblocked is not offered again, rather than settling.</summary>
    [Fact]
    public void UnknownPadUnblockedReturnsToNotAGamepad()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.Blocked));
        PadScript script = new PadScript()
            .Connect(0, 0, NoButtons, Centred, isKnownGamepad: false)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.None, now))
            .Until(2600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Equal(DeviceStatus.Blocked, Assert.Single(Of<DeviceAdded>(events)).Event.Snapshot.Status);
        AssertChangedThenSaved(At(events, 1500), DeviceStatus.NotAGamepad);
        Assert.Equal(DeviceStatus.NotAGamepad, StatusOf(claimer, 0));
    }

    /// <summary>A pad without a known gamepad layout that loses the player's trust is not offered again.</summary>
    [Fact]
    public void UnknownPadUntrustedReturnsToNotAGamepad()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = new PadScript()
            .Connect(0, 0, NoButtons, Centred, isKnownGamepad: false)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Trusted, now))
            .Call(3000, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.None, now))
            .Until(3100);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Contains(events, recorded => recorded.At == Ms(2500) && recorded.Event is DeviceChanged { Snapshot.Status: DeviceStatus.Ready });
        AssertChangedThenSaved(At(events, 3000), DeviceStatus.NotAGamepad);
        Assert.Equal(DeviceStatus.NotAGamepad, StatusOf(claimer, 0));
    }

    /// <summary>Blocking the identity of a holder that disconnected forgets it, so an unblocked return is not given the seat.</summary>
    [Fact]
    public void BlockingAnAwayHolderForgetsIt()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = CleanClaim(ConnectedPad(0), 0, 1500)
            .Disconnect(3000, 0)
            .Call(3200, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Blocked, now))
            .Call(3300, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.None, now))
            .Connect(3500, 3, NoButtons, Centred, identity: Pad0)
            .Until(4600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Empty(Of<SeatRestored>(events));
        Assert.Null(claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 3));
    }

    /// <summary>A pad without a known layout, trusted to claim and untrusted while it holds the seat, is not offered once released.</summary>
    [Fact]
    public void UntrustedLayoutlessHolderReturnsToNotAGamepadWhenReleased()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = LayoutlessPadClaimsThenIsUntrusted().Call(4500, (pads, now) => pads.ReleaseSeat(now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script, 0, 5000).Run(claimer);

        AssertClaimedThenUntrusted(events);
        AssertLostThenNotOffered(At(events, 4500), new SeatLost(0, SeatLossReason.Released));
        Assert.DoesNotContain(Of<ClaimStarted>(events), started => started.At > Ms(4500));
        Assert.Equal(DeviceStatus.NotAGamepad, StatusOf(claimer, 0));
    }

    /// <summary>A pad without a known layout, trusted to claim and untrusted while it holds the seat, is not offered once the seat moves.</summary>
    [Fact]
    public void UntrustedLayoutlessHolderReturnsToNotAGamepadWhenTheSeatMoves()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = CleanClaim(LayoutlessPadClaimsThenIsUntrusted().Connect(0, 1, NoButtons, Centred, identity: OtherPad), 1, 5000);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(script, 0, 6000).Run(claimer);

        AssertClaimedThenUntrusted(events);
        AssertLostThenNotOffered(At(events, 5600), new SeatMoved(0, 1));
        Assert.DoesNotContain(Of<ClaimStarted>(events), started => started.Event.Device == 0 && started.At > Ms(5600));
        Assert.Equal(DeviceStatus.NotAGamepad, StatusOf(claimer, 0));
    }

    /// <summary>A pad without a known layout given the seat by force is not offered once released.</summary>
    [Fact]
    public void ForceClaimedLayoutlessPadReturnsToNotAGamepad()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = new PadScript()
            .Connect(0, 0, NoButtons, Centred, isKnownGamepad: false)
            .Call(1500, (pads, now) => pads.ForceClaim(0, now))
            .Call(2000, (pads, now) => pads.ReleaseSeat(now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Equal(new SeatClaimed(0), Assert.Single(At(events, 1500)));
        AssertLostThenNotOffered(At(events, 2000), new SeatLost(0, SeatLossReason.Released));
        Assert.Equal(DeviceStatus.NotAGamepad, StatusOf(claimer, 0));
    }

    /// <summary>A pad blocked at connect keeps the buttons it had down, so unblocking it finds the stuck button.</summary>
    [Fact]
    public void UnblockedPadRemembersTheButtonsDownAtConnect()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.Blocked));
        PadScript script = new PadScript()
            .Connect(0, 0, [C], Centred)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.None, now))
            .Until(2600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, DeviceChanged Event) last = Of<DeviceChanged>(events)[^1];
        Assert.Equal((Ms(2500), DeviceStatus.Suspect, SuspectReason.StuckButton), (last.At, last.Event.Snapshot.Status, last.Event.Snapshot.Reasons));
        Assert.Equal<int>([C], last.Event.Snapshot.SuspectButtons);
    }

    /// <summary>A pad without a known layout keeps its axis values from connect, so trusting it records their rests.</summary>
    [Fact]
    public void TrustedLayoutlessPadRecordsTheAxisRestsFromConnect()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = new PadScript()
            .Connect(0, 0, NoButtons, [0f, -0.98f, 0f, 0f, 0f, 0f], isKnownGamepad: false)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Trusted, now))
            .Until(2600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        (TimeSpan At, DeviceChanged Event) last = Of<DeviceChanged>(events)[^1];
        Assert.Equal((Ms(2500), DeviceStatus.Ready), (last.At, last.Event.Snapshot.Status));
        Assert.Equal(new AxisRest(-0.98f, 0f, Known: false), claimer.RestOf(0, 1));
    }

    /// <summary>An away holder whose own rule is blocked through a wider identity is forgotten, so its unblocked return gets no seat.</summary>
    [Fact]
    public void AwayHolderBlockedThroughAWiderRuleIsForgotten()
    {
        DeviceIdentity holder = new(0x045E, 0x028E, "Pad", "S1");
        PadClaimer claimer = new(
            new PadClaimSettings([new DeviceRule(new DeviceIdentity(0x045E, 0x028E, "Pad", null), DeviceVerdict.None, [BlockedButton])])
        );
        PadScript script = CleanClaim(new PadScript().Connect(0, 0, NoButtons, Centred, identity: holder), 0, 1500)
            .Disconnect(3000, 0)
            .Call(3200, (pads, now) => pads.SetVerdict(new DeviceIdentity(0x045E, 0x028E, "Pad", "S2"), DeviceVerdict.Blocked, now))
            .Connect(3500, 3, NoButtons, Centred, identity: holder)
            .Call(4000, (pads, now) => pads.SetVerdict(holder, DeviceVerdict.None, now))
            .Until(5100);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Equal(DeviceStatus.Blocked, Of<DeviceAdded>(events).Single(entry => entry.Event.Snapshot.Device == 3).Event.Snapshot.Status);
        Assert.Contains(
            events,
            recorded => recorded.At == Ms(5000) && recorded.Event is DeviceChanged { Snapshot: { Device: 3, Status: DeviceStatus.Ready } }
        );
        Assert.Empty(Of<SeatRestored>(events));
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>
    /// An away holder whose own rule, once a Forget removes the rule that matched it, falls through to a blocked rule is
    /// forgotten, so its unblocked return gets no seat.
    /// </summary>
    [Fact]
    public void ForgetLettingAnAwayHolderFallThroughToABlockedRuleForgetsIt()
    {
        DeviceIdentity holder = new(0x045E, 0x028E, "Pad", "S1");
        DeviceIdentity twin = new(0x045E, 0x028E, "Other", "S1");
        DeviceRule wider = new(new DeviceIdentity(0, 0, "Pad", null), DeviceVerdict.Blocked, []);
        PadClaimer claimer = new(new PadClaimSettings([new DeviceRule(holder, DeviceVerdict.Trusted, []), wider]));
        PadScript script = CleanClaim(new PadScript().Connect(0, 0, NoButtons, Centred, identity: holder), 0, 1500)
            .Connect(2500, 1, NoButtons, Centred, identity: twin)
            .Disconnect(3000, 0)
            .Call(3200, (pads, now) => pads.Forget(new DeviceIdentity(0, 0, "Other", null), now))
            .Disconnect(3300, 1)
            .Connect(3500, 3, NoButtons, Centred, identity: holder)
            .Call(4000, (pads, now) => pads.SetVerdict(holder, DeviceVerdict.None, now))
            .Until(5100);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        DeviceRule kept = Assert.Single(Assert.Single(Of<SettingsChanged>(events), changed => changed.At == Ms(3200)).Event.Settings.Rules);
        Assert.Equal((wider.Device, DeviceVerdict.Blocked), (kept.Device, kept.Verdict));
        Assert.Equal(DeviceStatus.Blocked, Of<DeviceAdded>(events).Single(entry => entry.Event.Snapshot.Device == 3).Event.Snapshot.Status);
        Assert.Contains(
            events,
            recorded => recorded.At == Ms(5000) && recorded.Event is DeviceChanged { Snapshot: { Device: 3, Status: DeviceStatus.Ready } }
        );
        Assert.Empty(Of<SeatRestored>(events));
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>Forgetting a pad removes every rule of the connected pads it matches, so none falls through to a later rule.</summary>
    [Fact]
    public void ForgetRemovesEveryRuleOfTheMatchingPads()
    {
        DeviceIdentity pad = new(0x045E, 0x028E, "Pad 0", "S1");
        PadClaimer claimer = new(
            new PadClaimSettings([
                new DeviceRule(pad, DeviceVerdict.Trusted, []),
                new DeviceRule(new DeviceIdentity(0, 0, "Pad 0", null), DeviceVerdict.Blocked, []),
            ])
        );
        PadScript script = new PadScript()
            .Connect(0, 0, NoButtons, Centred, identity: pad)
            .Call(1500, (pads, now) => pads.Forget(new DeviceIdentity(0x045E, 0x028E, "Other", null), now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        AssertChangedThenSaved(At(events, 1500), DeviceStatus.Ready);
        DeviceSnapshot snapshot = Assert.Single(claimer.Devices);
        Assert.Equal((DeviceStatus.Ready, false), (snapshot.Status, snapshot.Trusted));
        Assert.Empty(claimer.Settings.Rules);
    }

    /// <summary>A forced claim clears the pad's suspicion, so it is ready and unsuspected once it loses the seat.</summary>
    [Fact]
    public void ForceClaimClearsSuspicion()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), 0, D, 1500, 1502)
            .Call(2000, (pads, now) => pads.ForceClaim(0, now))
            .Call(2500, (pads, now) => pads.ReleaseSeat(now));

        script.Run(claimer);

        DeviceSnapshot snapshot = Assert.Single(claimer.Devices);
        Assert.Equal((DeviceStatus.Ready, SuspectReason.None), (snapshot.Status, snapshot.Reasons));
        Assert.Empty(snapshot.SuspectButtons);
    }

    /// <summary>With unlisted pads accepted, a stray event from an index that disconnected does not admit it again.</summary>
    [Fact]
    public void StrayEventFromADisconnectedPadIsIgnored()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty, new ClaimOptions(UnlistedDevices: UnlistedDevicePolicy.Accept));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0).Disconnect(2000, 0).Press(2005, 0, A).Run(claimer);

        Assert.Empty(At(events, 2005));
        Assert.Empty(claimer.Devices);
    }

    /// <summary>Settings whose rules array was never initialised are refused at construction.</summary>
    [Fact]
    public void ClaimerRejectsDefaultRules()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() => new PadClaimer(new PadClaimSettings(default)));

        Assert.Contains("Rules", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A null rule is refused at construction.</summary>
    [Fact]
    public void ClaimerRejectsNullRule() => Assert.Throws<ArgumentException>(() => new PadClaimer(new PadClaimSettings([null!])));

    /// <summary>A rule with no device is refused at construction.</summary>
    [Fact]
    public void ClaimerRejectsNullDevice()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() =>
            new PadClaimer(new PadClaimSettings([new DeviceRule(null!, DeviceVerdict.Blocked, [])]))
        );

        Assert.Contains("device", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A rule whose blocked buttons were never initialised is refused at construction.</summary>
    [Fact]
    public void ClaimerRejectsDefaultBlockedButtons()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(() =>
            new PadClaimer(new PadClaimSettings([new DeviceRule(Pad0, DeviceVerdict.None, default)]))
        );

        Assert.Contains("BlockedButtons", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The claimer keeps each rule's blocked buttons distinct and sorted, so unblocking a button removes it.</summary>
    [Fact]
    public void ClaimerDeduplicatesBlockedButtons()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.None, 9, BlockedButton, 9, BlockedButton));

        Assert.Equal<int>([BlockedButton, 9], Assert.Single(claimer.Settings.Rules).BlockedButtons);

        claimer.SetButtonBlocked(Pad0, BlockedButton, blocked: false, Ms(0));

        Assert.Equal<int>([9], Assert.Single(claimer.Settings.Rules).BlockedButtons);
    }

    /// <summary>Blocking buttons one by one keeps the rule's blocked buttons distinct and sorted.</summary>
    [Fact]
    public void BlockedButtonsStaySortedAfterSetButtonBlocked()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        claimer.SetButtonBlocked(Pad0, BlockedButton, blocked: true, Ms(0));
        claimer.SetButtonBlocked(Pad0, C, blocked: true, Ms(0));
        claimer.SetButtonBlocked(Pad0, C, blocked: true, Ms(0));

        Assert.Equal<int>([C, BlockedButton], Assert.Single(claimer.Settings.Rules).BlockedButtons);
    }

    /// <summary>Forcing a claim for the seat holder itself raises nothing.</summary>
    [Fact]
    public void ForceClaimOnTheHolderIsSilent()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = CleanClaim(ConnectedPad(0), 0, 1500)
            .Call(2200, (pads, now) => pads.ForceClaim(0, now))
            .Run(claimer);

        Assert.Empty(At(events, 2200));
        Assert.Equal(0, claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Claimed, StatusOf(claimer, 0));
    }

    /// <summary>A forced claim takes the seat from every state but Blocked.</summary>
    /// <param name="state">The state the pad is in when the claim is forced.</param>
    [Theory]
    [InlineData(DeviceStatus.Settling)]
    [InlineData(DeviceStatus.AwaitingConfirm)]
    [InlineData(DeviceStatus.Suspect)]
    [InlineData(DeviceStatus.NotAGamepad)]
    public void ForceClaimFromEveryState(DeviceStatus state)
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        (PadScript script, int at) = state switch
        {
            DeviceStatus.Settling => (ConnectedPad(0), 500),
            DeviceStatus.AwaitingConfirm => (Hold(ConnectedPad(0), 0, A, 1500, 1600), 1700),
            DeviceStatus.Suspect => (Hold(ConnectedPad(0), 0, D, 1500, 1502), 1700),
            DeviceStatus.NotAGamepad => (new PadScript().Connect(0, 0, NoButtons, Centred, isKnownGamepad: false), 1700),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No script here reaches that state."),
        };
        script.Until(at).Run(claimer);
        Assert.Equal(state, StatusOf(claimer, 0));

        IReadOnlyList<ClaimEvent> forced = claimer.ForceClaim(0, Ms(at));

        Assert.Equal(new SeatClaimed(0), Assert.Single(forced));
        Assert.Equal(DeviceStatus.Claimed, StatusOf(claimer, 0));
        Assert.Equal(0, claimer.SeatDevice);
    }

    /// <summary>Forgetting a holder that disconnected forgets it, so its return is not given the seat.</summary>
    [Fact]
    public void ForgetForgetsAnAwayHolder()
    {
        PadClaimer claimer = new(RuleFor(DeviceVerdict.None, BlockedButton));
        PadScript script = CleanClaim(ConnectedPad(0), 0, 1500)
            .Disconnect(3000, 0)
            .Call(3200, (pads, now) => pads.Forget(Pad0, now))
            .Connect(3500, 3, NoButtons, Centred, identity: Pad0)
            .Until(4600);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Empty(Of<SeatRestored>(events));
        Assert.Null(claimer.SeatDevice);
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 3));
    }

    /// <summary>Re-sampling a pad that is not suspect raises nothing.</summary>
    [Fact]
    public void ResampleOnAPadNotSuspectIsSilent()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0).Call(1500, (pads, now) => pads.Resample(0, now)).Run(claimer);

        Assert.Empty(At(events, 1500));
        Assert.Equal(DeviceStatus.Ready, StatusOf(claimer, 0));
    }

    /// <summary>Unblocking a button that was never blocked raises nothing and adds no rule.</summary>
    [Fact]
    public void UnchangedButtonBlockIsSilent()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0)
            .Call(1500, (pads, now) => pads.SetButtonBlocked(Pad0, A, blocked: false, now))
            .Run(claimer);

        Assert.Empty(At(events, 1500));
        Assert.Empty(claimer.Settings.Rules);
    }

    /// <summary>Forgetting a pad with no rule raises nothing.</summary>
    [Fact]
    public void ForgetWithoutARuleIsSilent()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0).Call(1500, (pads, now) => pads.Forget(Pad0, now)).Run(claimer);

        Assert.Empty(At(events, 1500));
    }

    /// <summary>With unlisted pads accepted, an unlisted pad whose identity is blocked is admitted blocked and cannot claim.</summary>
    [Fact]
    public void UnlistedPadMatchingABlockedRuleIsAdmittedBlocked()
    {
        PadClaimSettings settings = new([new DeviceRule(new DeviceIdentity(0, 0, "Unlisted 7", null), DeviceVerdict.Blocked, [])]);
        PadClaimer claimer = new(settings, new ClaimOptions(UnlistedDevices: UnlistedDevicePolicy.Accept));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = Hold(Hold(new PadScript(), UnlistedPad, A, 100, 200), UnlistedPad, B, 400, 500)
            .Run(claimer);

        DeviceSnapshot added = Assert.Single(Of<DeviceAdded>(events)).Event.Snapshot;
        Assert.Equal((DeviceStatus.Blocked, true), (added.Status, added.Unlisted));
        Assert.Empty(Of<ClaimStarted>(events));
        Assert.Null(claimer.SeatDevice);
    }

    /// <summary>A connect on an unlisted pad's index replaces it with a listed pad that settles.</summary>
    [Fact]
    public void ConnectReplacesAnUnlistedPad()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty, new ClaimOptions(UnlistedDevices: UnlistedDevicePolicy.Accept));
        PadScript script = new PadScript().Press(100, UnlistedPad, A).Connect(200, UnlistedPad, NoButtons, Centred);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        List<ClaimEvent> atConnect = At(events, 200);
        Assert.Equal(2, atConnect.Count);
        Assert.Equal(new DeviceRemoved(UnlistedPad), atConnect[0]);
        DeviceSnapshot added = Assert.IsType<DeviceAdded>(atConnect[1]).Snapshot;
        Assert.Equal((PadScript.IdentityOf(UnlistedPad), DeviceStatus.Settling, false), (added.Identity, added.Status, added.Unlisted));
    }

    /// <summary>Trusting a ready pad keeps it ready and reports the change of trust.</summary>
    [Fact]
    public void TrustingAReadyPadReportsTheChange()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = ConnectedPad(0)
            .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Trusted, now))
            .Run(claimer);

        List<ClaimEvent> atTrust = At(events, 1500);
        AssertChangedThenSaved(atTrust, DeviceStatus.Ready);
        Assert.True(Assert.IsType<DeviceChanged>(atTrust[0]).Snapshot.Trusted);
    }

    /// <summary>Blocking a suspect pad clears its suspicion: the block, not the noise, now keeps it out.</summary>
    [Fact]
    public void BlockingASuspectPadClearsSuspicion()
    {
        PadClaimer claimer = new(PadClaimSettings.Empty);
        PadScript script = Hold(ConnectedPad(0), 0, D, 1500, 1502).Call(2000, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Blocked, now));

        IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events = script.Run(claimer);

        Assert.Contains(events, recorded => recorded.At == Ms(1502) && recorded.Event is DeviceChanged { Snapshot.Status: DeviceStatus.Suspect });
        DeviceSnapshot blocked = Assert.IsType<DeviceChanged>(At(events, 2000)[0]).Snapshot;
        Assert.Equal((DeviceStatus.Blocked, SuspectReason.None), (blocked.Status, blocked.Reasons));
        Assert.Empty(blocked.SuspectButtons);
    }

    private static PadScript LayoutlessPadClaimsThenIsUntrusted() =>
        CleanClaim(
                new PadScript()
                    .Connect(0, 0, NoButtons, Centred, isKnownGamepad: false)
                    .Call(1500, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.Trusted, now)),
                0,
                3000
            )
            .Call(4000, (pads, now) => pads.SetVerdict(Pad0, DeviceVerdict.None, now));

    private static void AssertClaimedThenUntrusted(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events)
    {
        Assert.Contains(
            events,
            recorded => recorded.At == Ms(2500) && recorded.Event is DeviceChanged { Snapshot: { Device: 0, Status: DeviceStatus.Ready } }
        );
        Assert.Equal(Ms(3600), Assert.Single(Of<SeatClaimed>(events), claimed => claimed.Event.Device == 0).At);
        DeviceSnapshot untrusted = Assert.IsType<DeviceChanged>(At(events, 4000)[0]).Snapshot;
        Assert.Equal((0, DeviceStatus.Claimed, false), (untrusted.Device, untrusted.Status, untrusted.Trusted));
    }

    private static void AssertLostThenNotOffered(List<ClaimEvent> events, ClaimEvent loss)
    {
        Assert.Equal(2, events.Count);
        Assert.Equal(loss, events[0]);
        DeviceSnapshot after = Assert.IsType<DeviceChanged>(events[1]).Snapshot;
        Assert.Equal((0, DeviceStatus.NotAGamepad), (after.Device, after.Status));
    }

    private static void AssertChangedThenSaved(List<ClaimEvent> events, DeviceStatus status)
    {
        Assert.Equal(2, events.Count);
        Assert.Equal(status, Assert.IsType<DeviceChanged>(events[0]).Snapshot.Status);
        Assert.IsType<SettingsChanged>(events[1]);
    }

    private static PadClaimSettings RuleFor(DeviceVerdict verdict, params int[] blockedButtons) =>
        new([new DeviceRule(Pad0, verdict, [.. blockedButtons])]);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static PadScript ConnectedPad(int device) => new PadScript().Connect(0, device, NoButtons, Centred);

    private static PadScript Hold(PadScript script, int device, int button, int down, int up) =>
        script.Press(down, device, button).Release(up, device, button);

    private static PadScript CleanClaim(PadScript script, int device, int start) =>
        Hold(Hold(script, device, A, start, start + 100), device, B, start + 500, start + 600);

    private static List<ClaimEvent> At(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events, int milliseconds) =>
        [.. events.Where(recorded => recorded.At == Ms(milliseconds)).Select(recorded => recorded.Event)];

    private static List<(TimeSpan At, T Event)> Of<T>(IReadOnlyList<(TimeSpan At, ClaimEvent Event)> events)
        where T : ClaimEvent => [.. events.Where(recorded => recorded.Event is T).Select(recorded => (recorded.At, (T)recorded.Event))];

    private static DeviceStatus StatusOf(PadClaimer claimer, int device) => claimer.Devices.Single(snapshot => snapshot.Device == device).Status;
}
