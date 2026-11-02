namespace PadClaim;

/// <summary>One connected pad's claim state: the quiet window, the buttons down, the axis rests and the gesture.</summary>
/// <param name="device">The engine's device index for this connection.</param>
/// <param name="identity">Who the pad is.</param>
/// <param name="tuning">The thresholds to measure against.</param>
internal sealed class DeviceTracker(int device, DeviceIdentity identity, ClaimTuning tuning)
{
    private readonly Dictionary<int, TimeSpan> pressedAt = [];
    private readonly Dictionary<int, AxisSample> axes = [];
    private Dictionary<int, AxisRest> rests = [];
    private DeviceStatus status = DeviceStatus.Settling;
    private TimeSpan stillSince;
    private int firstButton;
    private TimeSpan releasedAt;

    /// <summary>Gets the engine's device index for this connection.</summary>
    public int Device => device;

    /// <summary>Gets where the pad stands in the claim process.</summary>
    public DeviceStatus Status => status;

    /// <summary>Marks the device as one the claimer never offers, because it has no gamepad layout.</summary>
    public void MarkNotAGamepad() => status = DeviceStatus.NotAGamepad;

    /// <summary>Starts the quiet window from the state the engine reported at connect.</summary>
    /// <param name="buttonsDown">The buttons down at connect.</param>
    /// <param name="axisValues">Every axis's value at connect, by axis index; none is known until the engine sends it.</param>
    /// <param name="now">The time of the connect.</param>
    public void StartSettling(IReadOnlyList<int> buttonsDown, IReadOnlyList<float> axisValues, TimeSpan now)
    {
        status = DeviceStatus.Settling;
        stillSince = now;
        foreach (int button in buttonsDown)
        {
            pressedAt[button] = now;
        }

        for (int axis = 0; axis < axisValues.Count; axis++)
        {
            axes[axis] = new AxisSample(axisValues[axis], known: false);
        }
    }

    /// <summary>Gets the pad as the host sees it.</summary>
    /// <returns>The pad's snapshot.</returns>
    public DeviceSnapshot Snapshot() => new(device, identity, status, SuspectReason.None, [], Trusted: false, Unlisted: false);

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
            Press(button, now);
            return false;
        }

        return pressedAt.Remove(button, out TimeSpan downAt) && Release(button, downAt, now, events);
    }

    /// <summary>Takes an axis value. Axes only ever delay a claim, by restarting the quiet window.</summary>
    /// <param name="axis">The axis index.</param>
    /// <param name="value">The axis's new value.</param>
    /// <param name="now">The time of the value.</param>
    public void Axis(int axis, float value, TimeSpan now)
    {
        if (!axes.TryGetValue(axis, out AxisSample? sample))
        {
            sample = new AxisSample(value, known: true);
            axes[axis] = sample;
        }

        sample.Value = value;
        sample.Known = true;
        if (status != DeviceStatus.Settling)
        {
            return;
        }

        float offset = MathF.Abs(value - sample.Reference);
        if (offset > tuning.AxisStillBand)
        {
            RestartQuietWindow(now);
            return;
        }

        sample.Spread = MathF.Max(sample.Spread, offset);
    }

    /// <summary>Lets time pass: the quiet window may end, or the confirm window may run out.</summary>
    /// <param name="now">The current time.</param>
    /// <param name="events">The events the passing time causes, appended in order.</param>
    public void Tick(TimeSpan now, List<ClaimEvent> events)
    {
        if (status == DeviceStatus.Settling)
        {
            SettleIfStill(now, events);
        }
        else if (status == DeviceStatus.AwaitingConfirm)
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

    private void Press(int button, TimeSpan now)
    {
        if (pressedAt.TryAdd(button, now) && status == DeviceStatus.Settling)
        {
            RestartQuietWindow(now);
        }
    }

    private bool Release(int button, TimeSpan downAt, TimeSpan now, List<ClaimEvent> events)
    {
        switch (status)
        {
            case DeviceStatus.Settling:
                RestartQuietWindow(now);
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
        if (now - stillSince < tuning.QuietWindow || pressedAt.Count > 0)
        {
            return;
        }

        rests = axes.ToDictionary(entry => entry.Key, entry => new AxisRest(entry.Value.Reference, entry.Value.Spread, entry.Value.Known));
        status = DeviceStatus.Ready;
        events.Add(new DeviceChanged(Snapshot()));
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

    private void RestartQuietWindow(TimeSpan now)
    {
        stillSince = now;
        foreach (AxisSample sample in axes.Values)
        {
            sample.Reference = sample.Value;
            sample.Spread = 0f;
        }
    }

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
