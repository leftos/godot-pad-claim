namespace PadClaim;

/// <summary>The thresholds the claim logic measures against, with the defaults the design settled on.</summary>
public sealed record ClaimTuning
{
    /// <summary>
    /// How long a pad must stay still after connecting before it is offered: about one second, long enough to see a stuck
    /// or self-repeating button, short enough not to delay a player.
    /// </summary>
    public TimeSpan QuietWindow { get; init; } = TimeSpan.FromSeconds(1.0);

    /// <summary>
    /// How long the quiet window may run before a pad that never stills is marked suspect: far longer than a player
    /// fidgets after plugging in. Proposed, tunable.
    /// </summary>
    public TimeSpan SettleLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How far an axis may move and still count as still: above the resting drift of a worn stick, below a deliberate push.</summary>
    public float AxisStillBand { get; init; } = 0.05f;

    /// <summary>The shortest press that can be a claim: shorter presses come from a device toggling itself, not a finger.</summary>
    public TimeSpan MinClaimHold { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest hold that can still be a press, rather than an always-on button: a deliberate press rarely lasts longer.</summary>
    public TimeSpan MaxClaimHold { get; init; } = TimeSpan.FromSeconds(2.0);

    /// <summary>A press and release inside one input poll. Proposed: under a godot-mcp tap, about 16.7 ms.</summary>
    public TimeSpan SubHumanHold { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How long a button may stay down before the pad is suspect: longer than a player holds a button outside play.
    /// Proposed, tunable.
    /// </summary>
    public TimeSpan StuckHold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long after the first button's release the second button may be pressed to confirm: time for a player to find a
    /// second button, too short for two unrelated noisy buttons to line up often.
    /// </summary>
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromSeconds(3.0);

    /// <summary>How many presses make a button look self-repeating: four near-equal intervals rarely come from a hand. Proposed.</summary>
    public int RepeatPresses { get; init; } = 5;

    /// <summary>The interval jitter below which presses look periodic. Proposed: above one 60 Hz frame of quantisation.</summary>
    public TimeSpan RepeatJitterFloor { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// The interval jitter, as a fraction of the mean interval, below which presses look periodic: a hand's rhythm drifts
    /// by more. Proposed, tunable.
    /// </summary>
    public float RepeatJitterFraction { get; init; } = 0.04f;

    /// <summary>How far back repeating presses are counted from: long enough to catch a slow self-repeating button. Proposed.</summary>
    public TimeSpan RepeatHorizon { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long the seat holder must be idle before another pad may take the seat: a player mid-game is rarely idle that
    /// long, and a player switching pads waits no longer.
    /// </summary>
    public TimeSpan SeatStealIdle { get; init; } = TimeSpan.FromSeconds(2.0);

    /// <summary>The thresholds above, at their designed defaults.</summary>
    public static ClaimTuning Default { get; } = new();
}
