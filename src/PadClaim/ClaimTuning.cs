namespace PadClaim;

/// <summary>The thresholds the claim logic measures against, with the defaults the design settled on.</summary>
public sealed record ClaimTuning
{
    /// <summary>How long a pad must stay still after connecting before it is offered. Research §3.1: about one second.</summary>
    public TimeSpan QuietWindow { get; init; } = TimeSpan.FromSeconds(1.0);

    /// <summary>How long the quiet window may run before a pad that never stills is marked suspect. Proposed, tunable in DRIVE-7.</summary>
    public TimeSpan SettleLimit { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How far an axis may move and still count as still. DRIVE-7 plan §3; GODOT_CONVENTIONS listen-only-to-chosen-pads.</summary>
    public float AxisStillBand { get; init; } = 0.05f;

    /// <summary>The shortest press that can be a claim. Research §3.2.</summary>
    public TimeSpan MinClaimHold { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The longest hold that can still be a press, rather than an always-on button. Research §3.2.</summary>
    public TimeSpan MaxClaimHold { get; init; } = TimeSpan.FromSeconds(2.0);

    /// <summary>A press and release inside one input poll. Proposed: under a godot-mcp tap, about 16.7 ms.</summary>
    public TimeSpan SubHumanHold { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>How long a button may stay down before the pad is suspect. Proposed, tunable in DRIVE-7.</summary>
    public TimeSpan StuckHold { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long after the first button's release the second button may confirm. Owner ruling (two-button gesture).</summary>
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromSeconds(3.0);

    /// <summary>How many presses make a button look self-repeating. Proposed (research §3.3).</summary>
    public int RepeatPresses { get; init; } = 5;

    /// <summary>The interval jitter below which presses look periodic. Proposed: above one 60 Hz frame of quantisation.</summary>
    public TimeSpan RepeatJitterFloor { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The interval jitter, as a fraction of the mean interval, below which presses look periodic. Proposed, tunable in DRIVE-7.</summary>
    public float RepeatJitterFraction { get; init; } = 0.04f;

    /// <summary>How far back repeating presses are counted from. Proposed.</summary>
    public TimeSpan RepeatHorizon { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the seat holder must be idle before another pad may take the seat. DRIVE-7 plan §3.</summary>
    public TimeSpan SeatStealIdle { get; init; } = TimeSpan.FromSeconds(2.0);

    /// <summary>The thresholds above, at their designed defaults.</summary>
    public static ClaimTuning Default { get; } = new();
}
