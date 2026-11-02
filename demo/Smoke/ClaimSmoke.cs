using System.Globalization;
using Godot;
using PadClaim;
using PadClaim.GodotInput;

namespace PadClaimDemo.Smoke;

/// <summary>
/// Proves in the real engine, with no pad attached, that the adapter hands Godot's input to the claimer and that the
/// two-button claim works end to end, by injecting button presses and checking what the claimer decided.
/// </summary>
/// <remarks>
/// Run by <c>padclaim.ps1 smoke</c>. Every device index is one the engine never reported as connected, so the claimer
/// admits it unlisted and treats it as ready to claim. Each scenario gets its own index, because a sub-human tap marks
/// its device suspect. Time is the smoke's own, handed to the adapter as its clock, so however long a frame takes and
/// however long the machine starves the engine, a hold is the length the smoke says it is. The process exits 0 when all
/// three scenarios pass, 1 when one fails, 2 on the watchdog and 3 when an injected event does not reach <c>_Input</c>.
/// </remarks>
public partial class ClaimSmoke : Node
{
    /// <summary>The device the delivery check presses, before any scenario runs.</summary>
    private const int DeliveryDevice = 6;

    /// <summary>The device whose two one-frame taps must not claim.</summary>
    private const int TapsDevice = 8;

    /// <summary>The device whose two A presses must not claim.</summary>
    private const int DoubleDevice = 9;

    /// <summary>The device whose A then B gesture must take the seat.</summary>
    private const int ClaimDevice = 7;

    /// <summary>How many frames the delivery check waits for the injected press to reach <c>_Input</c>.</summary>
    private const int DeliveryFrames = 5;

    /// <summary>
    /// A tap: one frame at 60 Hz. Over the 10 ms sub-human hold, under the 50 ms minimum hold, so it can never claim.
    /// </summary>
    private const int TapMilliseconds = 16;

    /// <summary>A hold long enough to be a hand's: above the 50 ms minimum and far under the 2 s maximum.</summary>
    private const int HoldMilliseconds = 100;

    /// <summary>The pause between the two buttons, well inside the 3 s confirm window.</summary>
    private const int GapMilliseconds = 200;

    /// <summary>How long a scenario runs past its last release before it reads the recorded events.</summary>
    private const int AfterMilliseconds = 500;

    /// <summary>
    /// When to call the run hung. It counts real frame time and fires only when an await never completes, so it is
    /// sized by the hang it rules out rather than by how long the scenarios should take.
    /// </summary>
    private const double WatchdogSeconds = 300;

    private readonly PadClaimer claimer = new(
        PadClaimSettings.Empty,
        new ClaimOptions(RequireKnownGamepad: true, UnlistedDevices: UnlistedDevicePolicy.Accept)
    );

    private readonly List<ClaimEvent> raised = [];

    /// <summary>The adapter's clock, moved only by <see cref="AdvanceAsync"/>: starts a second in, so no stamp is zero.</summary>
    private TimeSpan clock = TimeSpan.FromSeconds(1);

    /// <summary>The last joypad button event <c>_Input</c> saw, or null before the first.</summary>
    private (int Device, JoyButton Button, bool Pressed)? seen;

    private GodotPadSource? source;
    private bool delivered;
    private bool finished;

    /// <inheritdoc/>
    public override void _Ready()
    {
        source = new GodotPadSource(claimer, () => clock);
        source.Raised += OnRaised;
        source.Start();
        GetTree().CreateTimer(WatchdogSeconds).Timeout += OnWatchdog;
        _ = RunAsync();
    }

    /// <inheritdoc/>
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventJoypadButton button)
        {
            if (button.Device == DeliveryDevice && button.Pressed)
            {
                delivered = true;
            }

            seen = (button.Device, button.ButtonIndex, button.Pressed);
        }

        if (source?.Feed(@event) == true)
        {
            GetViewport().SetInputAsHandled();
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta) => source?.Tick();

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (source is null)
        {
            return;
        }

        source.Raised -= OnRaised;
        source.Dispose();
        source = null;
    }

    private async Task RunAsync()
    {
        try
        {
            if (!await DeliveredAsync())
            {
                GD.PushError("smoke: parsed events do not reach _Input");
                Finish(3);
                return;
            }

            bool taps = await TapsAsync();
            bool doubleA = await DoubleAAsync();
            bool claim = await ClaimAsync();
            Finish(taps && doubleA && claim ? 0 : 1);
        }
        catch (Exception exception)
        {
            GD.PushError($"smoke: threw {exception}");
            Finish(1);
        }
    }

    private async Task<bool> DeliveredAsync()
    {
        Input.ParseInputEvent(Press(DeliveryDevice, JoyButton.A, true));
        for (int frame = 0; frame < DeliveryFrames && !delivered; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        return delivered;
    }

    private async Task<bool> TapsAsync()
    {
        await InjectAsync(TapsDevice, JoyButton.A, true);
        await AdvanceAsync(TapMilliseconds);
        await InjectAsync(TapsDevice, JoyButton.A, false);
        await InjectAsync(TapsDevice, JoyButton.B, true);
        await AdvanceAsync(TapMilliseconds);
        await InjectAsync(TapsDevice, JoyButton.B, false);
        string expected = string.Create(CultureInfo.InvariantCulture, $"no ClaimStarted and no SeatClaimed for device {TapsDevice}");
        return Check("taps", !ClaimStartedBy(TapsDevice) && !SeatClaimedBy(TapsDevice), expected);
    }

    private async Task<bool> DoubleAAsync()
    {
        await InjectAsync(DoubleDevice, JoyButton.A, true);
        await AdvanceAsync(HoldMilliseconds);
        await InjectAsync(DoubleDevice, JoyButton.A, false);
        await AdvanceAsync(GapMilliseconds);
        await InjectAsync(DoubleDevice, JoyButton.A, true);
        await AdvanceAsync(HoldMilliseconds);
        await InjectAsync(DoubleDevice, JoyButton.A, false);
        await AdvanceAsync(AfterMilliseconds);
        string expected = string.Create(CultureInfo.InvariantCulture, $"ClaimStarted for device {DoubleDevice} and no SeatClaimed for it");
        return Check("a-a", ClaimStartedBy(DoubleDevice) && !SeatClaimedBy(DoubleDevice), expected);
    }

    private async Task<bool> ClaimAsync()
    {
        await InjectAsync(ClaimDevice, JoyButton.A, true);
        await AdvanceAsync(HoldMilliseconds);
        await InjectAsync(ClaimDevice, JoyButton.A, false);
        await AdvanceAsync(GapMilliseconds);
        await InjectAsync(ClaimDevice, JoyButton.B, true);
        await AdvanceAsync(HoldMilliseconds);
        await InjectAsync(ClaimDevice, JoyButton.B, false);
        await AdvanceAsync(AfterMilliseconds);
        string expected = string.Create(CultureInfo.InvariantCulture, $"SeatClaimed by device {ClaimDevice}, with the seat on it");
        return Check("claim", SeatClaimedBy(ClaimDevice) && claimer.SeatDevice == ClaimDevice, expected);
    }

    /// <summary>Injects one button event and returns once <c>_Input</c> has handed it to the claimer.</summary>
    private async Task InjectAsync(int device, JoyButton button, bool pressed)
    {
        seen = null;
        Input.ParseInputEvent(Press(device, button, pressed));
        (int Device, JoyButton Button, bool Pressed) wanted = (device, button, pressed);
        while (seen != wanted)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    /// <summary>Moves the clock the adapter reads, then lets one frame tick the claimer at the new time.</summary>
    private async Task AdvanceAsync(int milliseconds)
    {
        clock += TimeSpan.FromMilliseconds(milliseconds);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private static InputEventJoypadButton Press(int device, JoyButton button, bool pressed) =>
        new()
        {
            Device = device,
            ButtonIndex = button,
            Pressed = pressed,
        };

    private bool ClaimStartedBy(int device) => raised.OfType<ClaimStarted>().Any(claimEvent => claimEvent.Device == device);

    private bool SeatClaimedBy(int device) => raised.OfType<SeatClaimed>().Any(claimEvent => claimEvent.Device == device);

    private bool Check(string name, bool passed, string expected)
    {
        if (passed)
        {
            GD.Print($"smoke: {name} ok");
            return true;
        }

        GD.PushError($"smoke: {name} FAILED: expected {expected} / {Describe()}");
        return false;
    }

    private string Describe()
    {
        string described = string.Join("; ", raised.Select(claimEvent => claimEvent.ToString()));
        return described.Length == 0 ? "no events" : described;
    }

    private void OnRaised(ClaimEvent claimEvent) => raised.Add(claimEvent);

    private void OnWatchdog()
    {
        if (finished)
        {
            return;
        }

        GD.Print("smoke: timed out");
        Finish(2);
    }

    private void Finish(int code)
    {
        if (finished)
        {
            return;
        }

        finished = true;
        GetTree().Quit(code);
    }
}
