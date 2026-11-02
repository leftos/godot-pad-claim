using System.Diagnostics.CodeAnalysis;

namespace PadClaim;

/// <summary>What happens to input from a device the claimer never saw connect.</summary>
public enum UnlistedDevicePolicy
{
    /// <summary>The input is dropped.</summary>
    Ignore,

    /// <summary>The device is admitted as ready, with no quiet window, and its input is processed.</summary>
    Accept,
}

/// <summary>How the host wants pads admitted.</summary>
/// <param name="RequireKnownGamepad">Whether a device the engine has no gamepad layout for is kept from claiming.</param>
/// <param name="UnlistedDevices">What happens to input from a device never seen to connect.</param>
public sealed record ClaimOptions(bool RequireKnownGamepad = true, UnlistedDevicePolicy UnlistedDevices = UnlistedDevicePolicy.Ignore);

/// <summary>
/// Decides which pad holds the seat. Fed timestamped samples from every connected pad, it returns the events each
/// sample causes; it never reads a clock.
/// </summary>
public sealed class PadClaimer
{
    private readonly ClaimOptions options;
    private readonly ClaimTuning tuning;
    private readonly Dictionary<int, DeviceTracker> devices = [];
    private readonly Seat seat;

    /// <summary>Creates a claimer with no pads connected and the seat vacant.</summary>
    /// <param name="settings">The player's decisions about pads.</param>
    /// <param name="options">How pads are admitted; the defaults when null.</param>
    /// <param name="tuning">The thresholds; <see cref="ClaimTuning.Default"/> when null.</param>
    public PadClaimer(PadClaimSettings settings, ClaimOptions? options = null, ClaimTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.options = options ?? new ClaimOptions();
        this.tuning = tuning ?? ClaimTuning.Default;
        seat = new Seat(this.tuning);
    }

    /// <summary>Gets every connected pad, in ascending device index.</summary>
    public IReadOnlyList<DeviceSnapshot> Devices => [.. Ordered().Select(tracker => tracker.Snapshot())];

    /// <summary>Gets the device index of the seat holder, or null when the seat is vacant.</summary>
    public int? SeatDevice => seat.Device;

    /// <summary>Gets or sets whether a completed gesture may take the seat; true by default.</summary>
    public bool ClaimsOpen
    {
        get => seat.ClaimsOpen;
        set => seat.ClaimsOpen = value;
    }

    /// <summary>Takes a pad's connection. A device index already connected is disconnected first.</summary>
    /// <param name="device">The engine's device index for this connection.</param>
    /// <param name="identity">Who the pad is.</param>
    /// <param name="isKnownGamepad">Whether the engine has a gamepad layout for the device.</param>
    /// <param name="buttonsDown">The buttons down at connect.</param>
    /// <param name="axes">Every axis's value at connect, by axis index.</param>
    /// <param name="now">The time of the connect.</param>
    /// <returns>The events the connect caused, in order.</returns>
    public IReadOnlyList<ClaimEvent> Connect(
        int device,
        DeviceIdentity identity,
        bool isKnownGamepad,
        IReadOnlyList<int> buttonsDown,
        IReadOnlyList<float> axes,
        TimeSpan now
    )
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(buttonsDown);
        ArgumentNullException.ThrowIfNull(axes);

        List<ClaimEvent> events = [];
        if (devices.ContainsKey(device))
        {
            Remove(device, events);
        }

        DeviceTracker tracker = new(device, identity, tuning);
        if (!isKnownGamepad && options.RequireKnownGamepad)
        {
            tracker.MarkNotAGamepad();
        }
        else
        {
            tracker.StartSettling(buttonsDown, axes, now);
        }

        devices[device] = tracker;
        events.Add(new DeviceAdded(tracker.Snapshot()));
        return events;
    }

    /// <summary>Takes a pad's disconnection.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="now">The time of the disconnect.</param>
    /// <returns>The events the disconnect caused, in order; none for a device not connected.</returns>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Every sample entry point takes the sample's time.")]
    public IReadOnlyList<ClaimEvent> Disconnect(int device, TimeSpan now)
    {
        List<ClaimEvent> events = [];
        if (devices.ContainsKey(device))
        {
            Remove(device, events);
        }

        return events;
    }

    /// <summary>Takes a button change.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="button">The button index.</param>
    /// <param name="pressed">Whether the button went down.</param>
    /// <param name="now">The time of the change.</param>
    /// <returns>The events the change caused, in order.</returns>
    public IReadOnlyList<ClaimEvent> Button(int device, int button, bool pressed, TimeSpan now)
    {
        List<ClaimEvent> events = [];
        if (!devices.TryGetValue(device, out DeviceTracker? tracker))
        {
            return events;
        }

        seat.NoteButton(device, button, pressed, now);
        if (tracker.Button(button, pressed, now, events))
        {
            seat.Attempt(tracker, now, events);
        }

        return events;
    }

    /// <summary>Takes an axis value. Axes never move a pad toward a claim.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="axis">The axis index.</param>
    /// <param name="value">The axis's new value.</param>
    /// <param name="now">The time of the value.</param>
    /// <returns>The events the value caused, in order.</returns>
    public IReadOnlyList<ClaimEvent> Axis(int device, int axis, float value, TimeSpan now)
    {
        if (devices.TryGetValue(device, out DeviceTracker? tracker) && tracker.Axis(axis, value, now))
        {
            seat.NoteAxisActivity(device, now);
        }

        return [];
    }

    /// <summary>Lets time pass for every pad, in ascending device index.</summary>
    /// <param name="now">The current time.</param>
    /// <returns>The events the passing time caused, in order.</returns>
    public IReadOnlyList<ClaimEvent> Tick(TimeSpan now)
    {
        List<ClaimEvent> events = [];
        foreach (DeviceTracker tracker in Ordered())
        {
            bool settling = tracker.Status == DeviceStatus.Settling;
            tracker.Tick(now, events);
            if (settling && tracker.Status == DeviceStatus.Ready)
            {
                seat.TryRestore(tracker, devices.Values, now, events);
            }
        }

        return events;
    }

    /// <summary>Vacates the seat at the host's request; the holder becomes ready and a holder that left is forgotten.</summary>
    /// <param name="now">The time of the release.</param>
    /// <returns>The events the release caused: the holder's seat loss, or none when the seat is vacant.</returns>
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "Every host operation takes the time it happens.")]
    public IReadOnlyList<ClaimEvent> ReleaseSeat(TimeSpan now)
    {
        List<ClaimEvent> events = [];
        seat.Release(events);
        return events;
    }

    /// <summary>Tells whether a button press or release reaches the game.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="button">The button index.</param>
    /// <returns>True for the seat device's buttons, except one held through the claim until its release.</returns>
    public bool AcceptsButton(int device, int button) => seat.AcceptsButton(device, button);

    /// <summary>Tells whether an axis value reaches the game.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="axis">The axis index.</param>
    /// <returns>True for the seat device's axes only.</returns>
    [SuppressMessage(
        "Style",
        "IDE0060:Remove unused parameter",
        Justification = "The host asks per axis; every axis of the seat device is accepted."
    )]
    public bool AcceptsAxis(int device, int axis) => seat.AcceptsAxes(device);

    /// <summary>Gets where an axis rested while the pad settled, for shaping its values.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="axis">The axis index.</param>
    /// <returns>The rest, or null when the pad is not connected, has not settled, or did not report the axis.</returns>
    public AxisRest? RestOf(int device, int axis) => devices.TryGetValue(device, out DeviceTracker? tracker) ? tracker.RestOf(axis) : null;

    private List<DeviceTracker> Ordered() => [.. devices.Values.OrderBy(tracker => tracker.Device)];

    private void Remove(int device, List<ClaimEvent> events)
    {
        if (devices.Remove(device, out DeviceTracker? tracker))
        {
            tracker.Disconnect(events);
            seat.Removed(tracker, events);
        }

        events.Add(new DeviceRemoved(device));
    }
}
