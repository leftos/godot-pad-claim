namespace PadClaim.Tests.Support;

/// <summary>
/// A timed script of pad samples, in milliseconds since the start, run against a <see cref="PadClaimer"/>. Running it
/// feeds the steps in time order, ticks at every 10 ms boundary between them and once after each step, and records
/// every event the claimer returns with the time it was returned at.
/// </summary>
public sealed class PadScript
{
    private const int TickMilliseconds = 10;

    private readonly List<Step> steps = [];

    /// <summary>Connects a pad.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <param name="device">The device index.</param>
    /// <param name="buttonsDown">The buttons already down at connect.</param>
    /// <param name="axes">Every axis's value at connect, by axis index.</param>
    /// <param name="isKnownGamepad">Whether the engine knows the pad's layout.</param>
    /// <param name="identity">Who the pad is; <see cref="IdentityOf"/> the device when null.</param>
    /// <returns>This script.</returns>
    public PadScript Connect(
        int t,
        int device,
        IReadOnlyList<int> buttonsDown,
        IReadOnlyList<float> axes,
        bool isKnownGamepad = true,
        DeviceIdentity? identity = null
    ) => Add(t, (claimer, now) => claimer.Connect(device, identity ?? IdentityOf(device), isKnownGamepad, buttonsDown, axes, now));

    /// <summary>Runs a host or settings-screen operation.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <param name="operation">The operation, given the claimer and the time.</param>
    /// <returns>This script.</returns>
    public PadScript Call(int t, Func<PadClaimer, TimeSpan, IReadOnlyList<ClaimEvent>> operation) => Add(t, operation);

    /// <summary>Presses a button.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <param name="device">The device index.</param>
    /// <param name="button">The button index.</param>
    /// <returns>This script.</returns>
    public PadScript Press(int t, int device, int button) => Add(t, (claimer, now) => claimer.Button(device, button, true, now));

    /// <summary>Releases a button.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <param name="device">The device index.</param>
    /// <param name="button">The button index.</param>
    /// <returns>This script.</returns>
    public PadScript Release(int t, int device, int button) => Add(t, (claimer, now) => claimer.Button(device, button, false, now));

    /// <summary>Moves an axis.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <param name="device">The device index.</param>
    /// <param name="axis">The axis index.</param>
    /// <param name="value">The axis's new value.</param>
    /// <returns>This script.</returns>
    public PadScript Axis(int t, int device, int axis, float value) => Add(t, (claimer, now) => claimer.Axis(device, axis, value, now));

    /// <summary>Disconnects a pad.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <param name="device">The device index.</param>
    /// <returns>This script.</returns>
    public PadScript Disconnect(int t, int device) => Add(t, (claimer, now) => claimer.Disconnect(device, now));

    /// <summary>Keeps ticking until a time with no sample of its own.</summary>
    /// <param name="t">The time in milliseconds.</param>
    /// <returns>This script.</returns>
    public PadScript Until(int t) => Add(t, (_, _) => []);

    /// <summary>Runs the script.</summary>
    /// <param name="claimer">The claimer to feed.</param>
    /// <returns>Every event the claimer returned, in order, with the time it was returned at.</returns>
    public IReadOnlyList<(TimeSpan At, ClaimEvent Event)> Run(PadClaimer claimer)
    {
        ArgumentNullException.ThrowIfNull(claimer);

        List<(TimeSpan At, ClaimEvent Event)> recorded = [];
        List<Step> ordered = [.. steps.OrderBy(step => step.At)];
        int previous = ordered.Count > 0 ? ordered[0].At : 0;

        foreach (Step step in ordered)
        {
            for (int boundary = (previous / TickMilliseconds + 1) * TickMilliseconds; boundary < step.At; boundary += TickMilliseconds)
            {
                Record(recorded, boundary, claimer.Tick(Ms(boundary)));
            }

            Record(recorded, step.At, step.Apply(claimer, Ms(step.At)));
            Record(recorded, step.At, claimer.Tick(Ms(step.At)));
            previous = step.At;
        }

        return recorded;
    }

    /// <summary>The identity the script gives a device.</summary>
    /// <param name="device">The device index.</param>
    /// <returns>An Xbox 360 pad's ids with a name naming the device.</returns>
    public static DeviceIdentity IdentityOf(int device) => new(0x045E, 0x028E, $"Pad {device}", null);

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static void Record(List<(TimeSpan At, ClaimEvent Event)> recorded, int at, IReadOnlyList<ClaimEvent> events)
    {
        foreach (ClaimEvent claimEvent in events)
        {
            recorded.Add((Ms(at), claimEvent));
        }
    }

    private PadScript Add(int t, Func<PadClaimer, TimeSpan, IReadOnlyList<ClaimEvent>> apply)
    {
        steps.Add(new Step(t, apply));
        return this;
    }

    private sealed record Step(int At, Func<PadClaimer, TimeSpan, IReadOnlyList<ClaimEvent>> Apply);
}
