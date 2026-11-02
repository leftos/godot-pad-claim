namespace PadClaim;

/// <summary>
/// One connected pad's claim state: the quiet window, the buttons down, the axis rests, the gesture and the noise that
/// made it suspect.
/// </summary>
/// <param name="device">The engine's device index for this connection.</param>
/// <param name="identity">Who the pad is.</param>
/// <param name="tuning">The thresholds to measure against.</param>
internal sealed class DeviceTracker(int device, DeviceIdentity identity, ClaimTuning tuning)
{
    private readonly Dictionary<int, TimeSpan> pressedAt = [];
    private readonly Dictionary<int, AxisSample> axes = [];
    private readonly SortedSet<int> suspectButtons = [];
    private readonly HashSet<int> downAtConnect = [];
    private readonly HashSet<int> blockedButtons = [];
    private NoiseDetector noise = new(tuning);
    private Dictionary<int, AxisRest> rests = [];
    private bool trusted;
    private bool unlisted;
    private DeviceStatus status = DeviceStatus.Settling;
    private SuspectReason reasons;
    private TimeSpan settleStart;
    private TimeSpan stillSince;
    private int firstButton;
    private TimeSpan releasedAt;

    /// <summary>Gets the engine's device index for this connection.</summary>
    public int Device => device;

    /// <summary>Gets who the pad is.</summary>
    public DeviceIdentity Identity => identity;

    /// <summary>Gets where the pad stands in the claim process.</summary>
    public DeviceStatus Status => status;

    /// <summary>Gets the buttons down now.</summary>
    public IReadOnlyCollection<int> ButtonsDown => pressedAt.Keys;

    /// <summary>
    /// Admits the pad as it connected: blocked when its rule blocks it, not offered when it lacks a gamepad layout the
    /// host requires and is not trusted, else settling from the state the engine reported.
    /// </summary>
    /// <param name="rule">The player's rule for the pad, or null when there is none.</param>
    /// <param name="lacksLayout">Whether the pad has no gamepad layout and the host requires one.</param>
    /// <param name="buttonsDown">The buttons down at connect.</param>
    /// <param name="axisValues">Every axis's value at connect, by axis index; none is known until the engine sends it.</param>
    /// <param name="now">The time of the connect.</param>
    public void Admit(DeviceRule? rule, bool lacksLayout, IReadOnlyList<int> buttonsDown, IReadOnlyList<float> axisValues, TimeSpan now)
    {
        UseRule(rule);
        if (rule?.Verdict == DeviceVerdict.Blocked)
        {
            status = DeviceStatus.Blocked;
        }
        else if (lacksLayout && !trusted)
        {
            status = DeviceStatus.NotAGamepad;
        }
        else
        {
            StartSettling(buttonsDown, axisValues, now);
        }
    }

    /// <summary>Admits a pad never seen to connect: ready at once with no quiet window, unless its rule blocks it.</summary>
    /// <param name="rule">The player's rule for the pad, or null when there is none.</param>
    public void AdmitUnlisted(DeviceRule? rule)
    {
        unlisted = true;
        UseRule(rule);
        status = rule?.Verdict == DeviceVerdict.Blocked ? DeviceStatus.Blocked : DeviceStatus.Ready;
    }

    /// <summary>
    /// Applies the player's changed rule: a blocked pad leaves the claim process, a pad leaving the block or trusted out
    /// of quarantine settles afresh, and any other pad keeps its state with the detectors following its trust.
    /// </summary>
    /// <param name="rule">The pad's rule after the change, or null when there is none.</param>
    /// <param name="now">The time of the change.</param>
    /// <param name="events">The events the change causes, appended in order.</param>
    public void ApplyRule(DeviceRule? rule, TimeSpan now, List<ClaimEvent> events)
    {
        DeviceStatus before = status;
        bool wasTrusted = trusted;
        UseRule(rule);
        if (rule?.Verdict == DeviceVerdict.Blocked)
        {
            status = DeviceStatus.Blocked;
        }
        else if (LeavesQuarantine(before))
        {
            Resettle(now);
        }

        if (status != before || trusted != wasTrusted)
        {
            events.Add(new DeviceChanged(Snapshot()));
        }
    }

    /// <summary>Samples a suspect pad afresh: its suspicion is cleared and the quiet window starts over.</summary>
    /// <param name="now">The time of the request.</param>
    /// <param name="events">The events the request causes, appended in order; none for a pad that is not suspect.</param>
    public void Resample(TimeSpan now, List<ClaimEvent> events)
    {
        if (status != DeviceStatus.Suspect)
        {
            return;
        }

        Resettle(now);
        events.Add(new DeviceChanged(Snapshot()));
    }

    /// <summary>Tells whether the player blocked a button of this pad.</summary>
    /// <param name="button">The button index.</param>
    /// <returns>True when the button never counts.</returns>
    public bool BlocksButton(int button) => blockedButtons.Contains(button);

    /// <summary>Gets the pad as the host sees it.</summary>
    /// <returns>The pad's snapshot.</returns>
    public DeviceSnapshot Snapshot() => new(device, identity, status, reasons, [.. suspectButtons], trusted, unlisted);

    /// <summary>Gets where an axis rested in the quiet window.</summary>
    /// <param name="axis">The axis index.</param>
    /// <returns>The rest, or null when the pad has not settled or the axis was not seen while it settled.</returns>
    public AxisRest? RestOf(int axis) => rests.GetValueOrDefault(axis);

    /// <summary>Takes a button change.</summary>
    /// <param name="button">The button index.</param>
    /// <param name="pressed">Whether the button went down.</param>
    /// <param name="now">The time of the change.</param>
    /// <param name="events">The events the change causes, appended in order.</param>
    /// <returns>True when the change completes the gesture, so the claimer must decide the seat.</returns>
    public bool Button(int button, bool pressed, TimeSpan now, List<ClaimEvent> events)
    {
        if (pressed)
        {
            Press(button, now, events);
            return false;
        }

        if (!pressedAt.Remove(button, out TimeSpan downAt))
        {
            ReleaseNeverSeenDown(button, now);
            return false;
        }

        bool pressTimeKnown = !downAtConnect.Remove(button);
        if (pressTimeKnown && DetectorsApply && noise.IsSubHuman(now - downAt))
        {
            Suspect(SuspectReason.SubHumanToggle, [button], events);
        }

        return Release(button, downAt, now, events);
    }

    /// <summary>Takes an axis value. Axes only ever delay a claim, by restarting the quiet window.</summary>
    /// <param name="axis">The axis index.</param>
    /// <param name="value">The axis's new value.</param>
    /// <param name="now">The time of the value.</param>
    /// <returns>
    /// True when the value is activity: beyond the axis's rest ± <see cref="ClaimTuning.AxisStillBand"/>, or, for an axis
    /// without a known rest, any change.
    /// </returns>
    public bool Axis(int axis, float value, TimeSpan now)
    {
        float? previous = null;
        if (axes.TryGetValue(axis, out AxisSample? sample))
        {
            previous = sample.Value;
        }
        else
        {
            sample = new AxisSample(value, known: true);
            axes[axis] = sample;
        }

        sample.Value = value;
        sample.Known = true;
        if (status == DeviceStatus.Settling)
        {
            SettleAxis(sample, value, now);
        }

        return IsAxisActivity(axis, value, previous);
    }

    /// <summary>While the pad settles, restarts the quiet window on an axis leaving its reference, else widens its spread.</summary>
    private void SettleAxis(AxisSample sample, float value, TimeSpan now)
    {
        float offset = MathF.Abs(value - sample.Reference);
        if (offset > tuning.AxisStillBand)
        {
            if (DetectorsApply)
            {
                noise.NoteAxisRestart();
            }

            RestartQuietWindow(now);
            return;
        }

        sample.Spread = MathF.Max(sample.Spread, offset);
    }

    /// <summary>Lets time pass: the quiet window may end, a button may count as stuck, or the confirm window may run out.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="events">The events the passing time causes, appended in order.</param>
    public void Tick(TimeSpan now, List<ClaimEvent> events)
    {
        if (status == DeviceStatus.Settling)
        {
            SettleIfStill(now, events);
            return;
        }

        MarkStuckButtons(now, events);
        if (status == DeviceStatus.AwaitingConfirm)
        {
            CancelIfTimedOut(now, events);
        }
    }

    /// <summary>Ends the connection, cancelling a started claim.</summary>
    /// <param name="events">The events the disconnect causes, appended in order.</param>
    public void Disconnect(List<ClaimEvent> events)
    {
        if (status == DeviceStatus.AwaitingConfirm)
        {
            events.Add(new ClaimCancelled(device, CancelReason.Disconnected));
        }
    }

    /// <summary>Records that the completed gesture took the seat.</summary>
    public void TakeSeat() => status = DeviceStatus.Claimed;

    /// <summary>Records that the completed gesture was refused the seat.</summary>
    public void RefuseSeat() => status = DeviceStatus.Ready;

    /// <summary>Records that the pad no longer holds the seat; it may claim again.</summary>
    public void LoseSeat() => status = DeviceStatus.Ready;

    /// <summary>
    /// Gets whether the noise detectors judge this pad: every pad but the seat holder, whose own player may hold and mash
    /// its buttons, a blocked pad and a pad the player trusts. Blocked buttons never reach them.
    /// </summary>
    private bool DetectorsApply => status is not (DeviceStatus.Claimed or DeviceStatus.Blocked) && !trusted;

    /// <summary>Gets whether a detector firing moves the pad to suspect: only from the states the gesture passes through.</summary>
    private bool CanTurnSuspect => status is DeviceStatus.Settling or DeviceStatus.Ready or DeviceStatus.AwaitingConfirm or DeviceStatus.Suspect;

    /// <summary>Gets whether a rule change from <paramref name="before"/> sends the pad back to settling.</summary>
    private bool LeavesQuarantine(DeviceStatus before) =>
        before == DeviceStatus.Blocked || (trusted && before is (DeviceStatus.Suspect or DeviceStatus.NotAGamepad));

    /// <summary>Takes the rule's trust and blocked buttons; a blocked button that is down is forgotten, as its release will be.</summary>
    private void UseRule(DeviceRule? rule)
    {
        trusted = rule?.Verdict == DeviceVerdict.Trusted;
        blockedButtons.Clear();
        if (rule is not null)
        {
            blockedButtons.UnionWith(rule.BlockedButtons);
        }

        foreach (int button in blockedButtons)
        {
            pressedAt.Remove(button);
            downAtConnect.Remove(button);
        }
    }

    private void StartSettling(IReadOnlyList<int> buttonsDown, IReadOnlyList<float> axisValues, TimeSpan now)
    {
        status = DeviceStatus.Settling;
        settleStart = now;
        stillSince = now;
        foreach (int button in buttonsDown.Where(button => !BlocksButton(button)))
        {
            pressedAt[button] = now;
            downAtConnect.Add(button);
        }

        for (int axis = 0; axis < axisValues.Count; axis++)
        {
            axes[axis] = new AxisSample(axisValues[axis], known: false);
        }
    }

    /// <summary>Starts the quiet window and the settle limit over, with the suspicion and the noise history cleared.</summary>
    private void Resettle(TimeSpan now)
    {
        status = DeviceStatus.Settling;
        settleStart = now;
        reasons = SuspectReason.None;
        suspectButtons.Clear();
        noise = new NoiseDetector(tuning);
        RestartQuietWindow(now);
    }

    private void Press(int button, TimeSpan now, List<ClaimEvent> events)
    {
        if (!pressedAt.TryAdd(button, now))
        {
            return;
        }

        if (status == DeviceStatus.Settling)
        {
            RestartQuietWindowByButton(button, now);
        }

        if (DetectorsApply && noise.PressIsRepeating(button, now))
        {
            Suspect(SuspectReason.RepeatingButton, [button], events);
        }
    }

    /// <summary>
    /// A release of a button not recorded as down (the connect-time read missed it) is still a button change while the pad
    /// settles, so it restarts the quiet window; outside settling it is ignored.
    /// </summary>
    private void ReleaseNeverSeenDown(int button, TimeSpan now)
    {
        if (status == DeviceStatus.Settling)
        {
            RestartQuietWindowByButton(button, now);
        }
    }

    private bool Release(int button, TimeSpan downAt, TimeSpan now, List<ClaimEvent> events)
    {
        switch (status)
        {
            case DeviceStatus.Settling:
                RestartQuietWindowByButton(button, now);
                return false;
            case DeviceStatus.Ready:
                StartIfHuman(button, now - downAt, now, events);
                return false;
            case DeviceStatus.AwaitingConfirm:
                return ReleaseWhileAwaiting(button, downAt, now, events);
            default:
                return false;
        }
    }

    private void StartIfHuman(int button, TimeSpan hold, TimeSpan now, List<ClaimEvent> events)
    {
        if (IsHumanHold(hold))
        {
            StartClaim(button, now, events);
        }
    }

    private bool ReleaseWhileAwaiting(int button, TimeSpan downAt, TimeSpan now, List<ClaimEvent> events)
    {
        if (!IsHumanHold(now - downAt))
        {
            return false;
        }

        if (button != firstButton && ConfirmsInTime(downAt))
        {
            return true;
        }

        StartClaim(button, now, events);
        return false;
    }

    private void StartClaim(int button, TimeSpan now, List<ClaimEvent> events)
    {
        status = DeviceStatus.AwaitingConfirm;
        firstButton = button;
        releasedAt = now;
        events.Add(new ClaimStarted(device, button));
    }

    private void SettleIfStill(TimeSpan now, List<ClaimEvent> events)
    {
        if (now - stillSince >= tuning.QuietWindow)
        {
            EndQuietWindow(events);
        }
        else if (now - settleStart >= tuning.SettleLimit && DetectorsApply)
        {
            Suspect(SuspectReason.NeverSettled | noise.SettleCulprits, noise.RestartingButtons, events);
        }
    }

    private void EndQuietWindow(List<ClaimEvent> events)
    {
        if (pressedAt.Count > 0 && DetectorsApply)
        {
            Suspect(SuspectReason.StuckButton, pressedAt.Keys, events);
            return;
        }

        rests = axes.ToDictionary(entry => entry.Key, entry => new AxisRest(entry.Value.Reference, entry.Value.Spread, entry.Value.Known));
        status = DeviceStatus.Ready;
        events.Add(new DeviceChanged(Snapshot()));
    }

    private void MarkStuckButtons(TimeSpan now, List<ClaimEvent> events)
    {
        if (!DetectorsApply)
        {
            return;
        }

        List<int> stuck = [.. pressedAt.Where(entry => noise.IsStuck(entry.Value, now)).Select(entry => entry.Key)];
        if (stuck.Count > 0)
        {
            Suspect(SuspectReason.StuckButton, stuck, events);
        }
    }

    private void Suspect(SuspectReason reason, IEnumerable<int> buttons, List<ClaimEvent> events)
    {
        if (!CanTurnSuspect)
        {
            return;
        }

        if (status == DeviceStatus.AwaitingConfirm)
        {
            events.Add(new ClaimCancelled(device, CancelReason.DeviceSuspect));
        }

        bool changed = status != DeviceStatus.Suspect || (reasons | reason) != reasons;
        status = DeviceStatus.Suspect;
        reasons |= reason;
        foreach (int button in buttons)
        {
            changed |= suspectButtons.Add(button);
        }

        if (changed)
        {
            events.Add(new DeviceChanged(Snapshot()));
        }
    }

    private void CancelIfTimedOut(TimeSpan now, List<ClaimEvent> events)
    {
        if (now - releasedAt <= tuning.ConfirmWindow || pressedAt.Any(entry => entry.Key != firstButton && ConfirmsInTime(entry.Value)))
        {
            return;
        }

        status = DeviceStatus.Ready;
        events.Add(new ClaimCancelled(device, CancelReason.Timeout));
    }

    private void RestartQuietWindowByButton(int button, TimeSpan now)
    {
        if (DetectorsApply)
        {
            noise.NoteButtonRestart(button);
        }

        RestartQuietWindow(now);
    }

    private void RestartQuietWindow(TimeSpan now)
    {
        stillSince = now;
        foreach (AxisSample sample in axes.Values)
        {
            sample.Reference = sample.Value;
            sample.Spread = 0f;
        }
    }

    private bool IsAxisActivity(int axis, float value, float? previous) =>
        rests.TryGetValue(axis, out AxisRest? rest) && rest.Known ? MathF.Abs(value - rest.Centre) > tuning.AxisStillBand : previous != value;

    private bool IsHumanHold(TimeSpan hold) => hold >= tuning.MinClaimHold && hold <= tuning.MaxClaimHold;

    private bool ConfirmsInTime(TimeSpan downAt) => downAt >= releasedAt && downAt - releasedAt <= tuning.ConfirmWindow;

    private sealed class AxisSample(float value, bool known)
    {
        public float Value { get; set; } = value;

        public float Reference { get; set; } = value;

        public float Spread { get; set; }

        public bool Known { get; set; } = known;
    }
}
