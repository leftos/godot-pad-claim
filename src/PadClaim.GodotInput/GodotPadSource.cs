using Godot;

namespace PadClaim.GodotInput;

/// <summary>
/// Feeds Godot's joypad input into a <see cref="PadClaimer"/> and hands back the events it decides.
/// </summary>
/// <remarks>
/// The adapter owns no claim logic: every decision is the claimer's. The host calls <see cref="Start"/> once, passes
/// its input events to <see cref="Feed"/>, calls <see cref="Tick"/> from every frame, and disposes the source when its
/// node leaves the tree.
/// </remarks>
public sealed class GodotPadSource : IDisposable
{
    private readonly PadClaimer claimer;
    private readonly Func<TimeSpan> clock;
    private readonly HashSet<int> connectedDevices = [];
    private bool started;
    private bool disposed;

    /// <summary>Creates a source that feeds the given claimer, stamping every event with the engine's clock.</summary>
    /// <param name="claimer">The engine-free claimer that decides which pad holds the seat.</param>
    /// <exception cref="ArgumentNullException"><paramref name="claimer"/> is null.</exception>
    public GodotPadSource(PadClaimer claimer)
        : this(claimer, Now) { }

    /// <summary>Creates a source that stamps every event with a clock the host supplies.</summary>
    /// <param name="claimer">The engine-free claimer that decides which pad holds the seat.</param>
    /// <param name="clock">
    /// The time each event is stamped with, for a host or a test that drives time itself. It must never go backwards.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="claimer"/> or <paramref name="clock"/> is null.
    /// </exception>
    public GodotPadSource(PadClaimer claimer, Func<TimeSpan> clock)
    {
        ArgumentNullException.ThrowIfNull(claimer);
        ArgumentNullException.ThrowIfNull(clock);
        this.claimer = claimer;
        this.clock = clock;
    }

    /// <summary>Raised for every event the claimer returns, in the order it returns them.</summary>
    public event Action<ClaimEvent>? Raised;

    /// <summary>
    /// Connects every pad already present that the claimer does not list, then subscribes to later connects and
    /// disconnects.
    /// </summary>
    /// <exception cref="InvalidOperationException">The source was already started.</exception>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            throw new InvalidOperationException("The source was already started.");
        }

        Input.JoyConnectionChanged += OnJoyConnectionChanged;
        started = true;
        TimeSpan now = clock();
        foreach (int device in Input.GetConnectedJoypads())
        {
            if (disposed)
            {
                return;
            }

            Adopt(device, now);
        }
    }

    /// <summary>Takes one input event and reports whether the host should swallow it.</summary>
    /// <param name="inputEvent">The event from the host's input callback.</param>
    /// <returns>
    /// True for a joypad button or motion event the claimer does not accept, so the host marks it handled; false for
    /// anything else.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="inputEvent"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    public bool Feed(InputEvent inputEvent)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(inputEvent);

        TimeSpan now = clock();
        switch (inputEvent)
        {
            case InputEventJoypadButton button:
                Raise(claimer.Button(button.Device, (int)button.ButtonIndex, button.Pressed, now));
                return !claimer.AcceptsButton(button.Device, (int)button.ButtonIndex);
            case InputEventJoypadMotion motion:
                Raise(claimer.Axis(motion.Device, (int)motion.Axis, motion.AxisValue, now));
                return !claimer.AcceptsAxis(motion.Device, (int)motion.Axis);
            default:
                return false;
        }
    }

    /// <summary>Lets time pass for every pad, at the host's frame rate.</summary>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    public void Tick()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Raise(claimer.Tick(clock()));
    }

    /// <summary>
    /// Stops listening for joypad connections. A second call does nothing.
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (started)
        {
            Input.JoyConnectionChanged -= OnJoyConnectionChanged;
            started = false;
        }

        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static TimeSpan Now() => TimeSpan.FromMicroseconds((long)Time.GetTicksUsec());

    private void OnJoyConnectionChanged(long device, bool connected)
    {
        if (disposed)
        {
            return;
        }

        TimeSpan now = clock();
        int index = (int)device;
        if (connected)
        {
            if (connectedDevices.Add(index))
            {
                Connect(index, now);
            }
        }
        else
        {
            connectedDevices.Remove(index);
            Raise(claimer.Disconnect(index, now));
        }
    }

    private void Adopt(int device, TimeSpan now)
    {
        if (connectedDevices.Add(device) && !claimer.Devices.Any(snapshot => snapshot.Device == device))
        {
            Connect(device, now);
        }
    }

    private void Connect(int device, TimeSpan now)
    {
        Dictionary<string, string> info = [];
        foreach (KeyValuePair<Variant, Variant> entry in Input.GetJoyInfo(device))
        {
            info[entry.Key.ToString()] = entry.Value.ToString();
        }

        List<int> buttonsDown = [];
        for (int button = 0; button < (int)JoyButton.Max; button++)
        {
            if (Input.IsJoyButtonPressed(device, (JoyButton)button))
            {
                buttonsDown.Add(button);
            }
        }

        List<float> axes = [];
        for (int axis = 0; axis < (int)JoyAxis.Max; axis++)
        {
            axes.Add(Input.GetJoyAxis(device, (JoyAxis)axis));
        }

        Raise(claimer.Connect(device, DeviceIdentity.FromJoyInfo(info, Input.GetJoyName(device)), Input.IsJoyKnown(device), buttonsDown, axes, now));
    }

    private void Raise(IReadOnlyList<ClaimEvent> events)
    {
        foreach (ClaimEvent claimEvent in events)
        {
            if (disposed)
            {
                return;
            }

            Raised?.Invoke(claimEvent);
        }
    }
}
