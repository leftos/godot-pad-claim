namespace PadClaim;

/// <summary>
/// Judges one pad's input for signs of a device sending input on its own: sub-human toggles, stuck buttons,
/// self-repeating buttons and the inputs that kept the quiet window from ending.
/// </summary>
/// <param name="tuning">The thresholds to measure against.</param>
internal sealed class NoiseDetector(ClaimTuning tuning)
{
    private readonly Dictionary<int, Queue<TimeSpan>> presses = [];
    private readonly SortedSet<int> restartingButtons = [];
    private bool axisRestarted;

    /// <summary>Gets why the pad never settled: an axis, a button, or both kept restarting the quiet window.</summary>
    public SuspectReason SettleCulprits =>
        (axisRestarted ? SuspectReason.JitteryAxis : SuspectReason.None)
        | (restartingButtons.Count > 0 ? SuspectReason.RepeatingButton : SuspectReason.None);

    /// <summary>Gets the buttons that restarted the quiet window, in ascending order.</summary>
    public IReadOnlyCollection<int> RestartingButtons => restartingButtons;

    /// <summary>Records a press and judges whether the button is repeating itself.</summary>
    /// <param name="button">The button index.</param>
    /// <param name="now">The time of the press.</param>
    /// <returns>True when the button's last presses came at a near-constant period.</returns>
    public bool PressIsRepeating(int button, TimeSpan now)
    {
        if (!presses.TryGetValue(button, out Queue<TimeSpan>? history))
        {
            history = new Queue<TimeSpan>();
            presses[button] = history;
        }

        history.Enqueue(now);
        while (history.Count > tuning.RepeatPresses)
        {
            history.Dequeue();
        }

        return IsPeriodic([.. history]);
    }

    /// <summary>Judges whether a release came faster than a finger can press.</summary>
    /// <param name="hold">How long the button was down.</param>
    /// <returns>True when the hold is shorter than <see cref="ClaimTuning.SubHumanHold"/>.</returns>
    public bool IsSubHuman(TimeSpan hold) => hold < tuning.SubHumanHold;

    /// <summary>Judges whether a button has been down for the stuck limit.</summary>
    /// <param name="downAt">When the button went down.</param>
    /// <param name="now">The current time.</param>
    /// <returns>True when the button has been down for at least <see cref="ClaimTuning.StuckHold"/>.</returns>
    public bool IsStuck(TimeSpan downAt, TimeSpan now) => now - downAt >= tuning.StuckHold;

    /// <summary>Records that an axis restarted the quiet window.</summary>
    public void NoteAxisRestart() => axisRestarted = true;

    /// <summary>Records that a button restarted the quiet window.</summary>
    /// <param name="button">The button index.</param>
    public void NoteButtonRestart(int button) => restartingButtons.Add(button);

    private bool IsPeriodic(List<TimeSpan> times)
    {
        if (times.Count < Math.Max(tuning.RepeatPresses, 2) || times[^1] - times[0] > tuning.RepeatHorizon)
        {
            return false;
        }

        TimeSpan mean = (times[^1] - times[0]) / (times.Count - 1);
        long fractionTicks = (long)Math.Round(mean.Ticks * (double)tuning.RepeatJitterFraction);
        var bound = TimeSpan.FromTicks(Math.Max(tuning.RepeatJitterFloor.Ticks, fractionTicks));
        for (int index = 1; index < times.Count; index++)
        {
            if ((times[index] - times[index - 1] - mean).Duration() > bound)
            {
                return false;
            }
        }

        return true;
    }
}
