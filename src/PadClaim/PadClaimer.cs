using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

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
    private PadClaimSettings settings;

    /// <summary>Creates a claimer with no pads connected and the seat vacant.</summary>
    /// <param name="settings">The player's decisions about pads.</param>
    /// <param name="options">How pads are admitted; the defaults when null.</param>
    /// <param name="tuning">The thresholds; <see cref="ClaimTuning.Default"/> when null.</param>
    public PadClaimer(PadClaimSettings settings, ClaimOptions? options = null, ClaimTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.options = options ?? new ClaimOptions();
        this.tuning = tuning ?? ClaimTuning.Default;
        seat = new Seat(this.tuning);
    }

    /// <summary>Gets the player's decisions about pads, as changed by the settings operations.</summary>
    public PadClaimSettings Settings => settings;

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
        tracker.Admit(RuleFor(identity), !isKnownGamepad && options.RequireKnownGamepad, buttonsDown, axes, now);
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

    /// <summary>Takes a button change; a button the player blocked is dropped.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="button">The button index.</param>
    /// <param name="pressed">Whether the button went down.</param>
    /// <param name="now">The time of the change.</param>
    /// <returns>The events the change caused, in order.</returns>
    public IReadOnlyList<ClaimEvent> Button(int device, int button, bool pressed, TimeSpan now)
    {
        List<ClaimEvent> events = [];
        DeviceTracker? tracker = Find(device, events);
        if (tracker is null || tracker.BlocksButton(button))
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
    /// <returns>The events the value caused, in order: only an unlisted pad's admission.</returns>
    public IReadOnlyList<ClaimEvent> Axis(int device, int axis, float value, TimeSpan now)
    {
        List<ClaimEvent> events = [];
        DeviceTracker? tracker = Find(device, events);
        if (tracker is not null && tracker.Axis(axis, value, now))
        {
            seat.NoteAxisActivity(device, now);
        }

        return events;
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

    /// <summary>Gives a pad the seat unconditionally, at the settings screen's request.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="now">The time of the request.</param>
    /// <returns>
    /// SeatClaimed on a vacant seat or SeatMoved from the holder, which becomes ready; none for a pad not connected,
    /// blocked or already holding the seat.
    /// </returns>
    public IReadOnlyList<ClaimEvent> ForceClaim(int device, TimeSpan now)
    {
        List<ClaimEvent> events = [];
        if (devices.TryGetValue(device, out DeviceTracker? tracker) && tracker.Status != DeviceStatus.Blocked && seat.Device != device)
        {
            seat.Force(tracker, now, events);
        }

        return events;
    }

    /// <summary>Samples a suspect pad afresh: its suspicion is cleared and it settles again.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="now">The time of the request.</param>
    /// <returns>The pad's change to settling; none for a pad not connected or not suspect.</returns>
    public IReadOnlyList<ClaimEvent> Resample(int device, TimeSpan now)
    {
        List<ClaimEvent> events = [];
        if (devices.TryGetValue(device, out DeviceTracker? tracker))
        {
            tracker.Resample(now, events);
        }

        return events;
    }

    /// <summary>Records the player's verdict on a pad and applies it at once to every connected pad it matches.</summary>
    /// <param name="device">The pad's identity.</param>
    /// <param name="verdict">The verdict.</param>
    /// <param name="now">The time of the change.</param>
    /// <returns>The pads' changes, then SettingsChanged; none when the verdict was already in force.</returns>
    public IReadOnlyList<ClaimEvent> SetVerdict(DeviceIdentity device, DeviceVerdict verdict, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Update(device, rule => rule with { Verdict = verdict }, now);
    }

    /// <summary>Blocks or unblocks one button of a pad and applies it at once to every connected pad it matches.</summary>
    /// <param name="device">The pad's identity.</param>
    /// <param name="button">The button index.</param>
    /// <param name="blocked">Whether the button is blocked.</param>
    /// <param name="now">The time of the change.</param>
    /// <returns>The pads' changes, then SettingsChanged; none when the button already stood that way.</returns>
    public IReadOnlyList<ClaimEvent> SetButtonBlocked(DeviceIdentity device, int button, bool blocked, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Update(device, rule => rule with { BlockedButtons = WithButton(rule.BlockedButtons, button, blocked) }, now);
    }

    /// <summary>Removes every rule for a pad, so it is treated by the noise checks alone, and applies it at once.</summary>
    /// <param name="device">The pad's identity.</param>
    /// <param name="now">The time of the change.</param>
    /// <returns>The pads' changes, then SettingsChanged; none when no rule matched.</returns>
    public IReadOnlyList<ClaimEvent> Forget(DeviceIdentity device, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(device);

        ImmutableArray<DeviceRule> kept = [.. settings.Rules.Where(rule => !rule.Device.Matches(device))];
        return kept.Length == settings.Rules.Length ? [] : Change(new PadClaimSettings(kept), now);
    }

    /// <summary>Tells whether a button press or release reaches the game.</summary>
    /// <param name="device">The pad's device index.</param>
    /// <param name="button">The button index.</param>
    /// <returns>True for the seat device's buttons, except a blocked one and one held through the claim until its release.</returns>
    public bool AcceptsButton(int device, int button) =>
        seat.AcceptsButton(device, button) && devices.TryGetValue(device, out DeviceTracker? tracker) && !tracker.BlocksButton(button);

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

    private DeviceRule? RuleFor(DeviceIdentity identity) => settings.Rules.FirstOrDefault(rule => rule.Device.Matches(identity));

    /// <summary>Finds a connected pad, admitting one never seen to connect when the host accepts unlisted pads.</summary>
    private DeviceTracker? Find(int device, List<ClaimEvent> events)
    {
        if (devices.TryGetValue(device, out DeviceTracker? tracker))
        {
            return tracker;
        }

        if (options.UnlistedDevices != UnlistedDevicePolicy.Accept)
        {
            return null;
        }

        DeviceIdentity identity = new(0, 0, string.Create(CultureInfo.InvariantCulture, $"Unlisted {device}"), null);
        tracker = new DeviceTracker(device, identity, tuning);
        tracker.AdmitUnlisted(RuleFor(identity));
        devices[device] = tracker;
        events.Add(new DeviceAdded(tracker.Snapshot()));
        return tracker;
    }

    private static ImmutableArray<int> WithButton(ImmutableArray<int> buttons, int button, bool blocked)
    {
        if (buttons.Contains(button) == blocked)
        {
            return buttons;
        }

        return blocked ? buttons.Add(button) : buttons.Remove(button);
    }

    private int IndexOfRule(DeviceIdentity identity)
    {
        for (int index = 0; index < settings.Rules.Length; index++)
        {
            if (settings.Rules[index].Device.Matches(identity))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Updates the first rule matching the pad, keeping its stored identity, or appends a new rule for the pad; nothing
    /// happens when the update changes nothing.
    /// </summary>
    private List<ClaimEvent> Update(DeviceIdentity device, Func<DeviceRule, DeviceRule> update, TimeSpan now)
    {
        int index = IndexOfRule(device);
        DeviceRule current = index < 0 ? new DeviceRule(device, DeviceVerdict.None, []) : settings.Rules[index];
        DeviceRule updated = update(current);
        if (current.Verdict == updated.Verdict && current.BlockedButtons.SequenceEqual(updated.BlockedButtons))
        {
            return [];
        }

        ImmutableArray<DeviceRule> rules = index < 0 ? settings.Rules.Add(updated) : settings.Rules.SetItem(index, updated);
        return Change(new PadClaimSettings(rules), now);
    }

    /// <summary>Takes the new settings and applies each connected pad's rule to it, in ascending device index.</summary>
    private List<ClaimEvent> Change(PadClaimSettings changed, TimeSpan now)
    {
        settings = changed;
        List<ClaimEvent> events = [];
        foreach (DeviceTracker tracker in Ordered())
        {
            DeviceRule? rule = RuleFor(tracker.Identity);
            if (rule?.Verdict == DeviceVerdict.Blocked)
            {
                seat.Blocked(tracker, events);
            }

            tracker.ApplyRule(rule, now, events);
        }

        events.Add(new SettingsChanged(settings));
        return events;
    }

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
