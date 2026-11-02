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
    private const int D = 3;
    private const int BlockedButton = 7;
    private const int UnlistedPad = 7;

    private static readonly int[] NoButtons = [];
    private static readonly float[] Centred = [0f, 0f, 0f, 0f, 0f, 0f];
    private static readonly DeviceIdentity Pad0 = PadScript.IdentityOf(0);

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
