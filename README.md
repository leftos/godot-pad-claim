# godot-pad-claim

Gamepad claiming for Godot 4 C# that noisy controllers cannot fool.

A Godot game that must listen only to the pad the player chose has a problem most input code does not see: flight-sim panels, button boxes and worn pads with stuck or self-repeating buttons look like a pad pressing "Start" forever, and they take over the player's seat. This library watches each device, lets a player claim a pad only by a deliberate two-button gesture, keeps noisy devices out of the claim and lets the host persist a blocklist.

## Status

0.1.0, unreleased, API not stable. career-driver is the first consumer.

## Requirements

- Godot 4.7 .NET
- .NET 10

## Install

The library is not packaged yet. Clone it beside your game and add a project reference to the adapter, which brings the engine-free core `PadClaim` with it:

```xml
<ItemGroup>
  <ProjectReference Include="..\godot-pad-claim\src\PadClaim.GodotInput\PadClaim.GodotInput.csproj" />
</ItemGroup>
```

The core, `src/PadClaim`, references only the .NET framework and can be used on its own: feed it timestamped samples and it returns events. The adapter, `src/PadClaim.GodotInput`, is the Godot side.

## Usage

Create one `PadClaimer` with the settings the host saved, then host a `GodotPadSource` from a node. The source is a plain object, not a `Node`, so the host forwards its callbacks:

```csharp
public partial class PadHost : Node
{
    private readonly PadClaimer claimer = new(PadClaimSettings.Empty);
    private GodotPadSource? source;

    public override void _Ready()
    {
        source = new GodotPadSource(claimer);
        source.Raised += OnRaised;   // subscribe before Start, so no event from the pads already present is missed
        source.Start();
    }

    public override void _Input(InputEvent @event)
    {
        if (source?.Feed(@event) == true)
        {
            GetViewport().SetInputAsHandled();   // a pad that is not the seat's must not move GUI focus
        }
    }

    public override void _Process(double delta) => source?.Tick();

    public override void _ExitTree()
    {
        if (source is null) return;
        source.Raised -= OnRaised;
        source.Dispose();   // removes the handler from the static Input.JoyConnectionChanged
        source = null;
    }

    private void OnRaised(ClaimEvent claimEvent)
    {
        switch (claimEvent)
        {
            case SeatClaimed claimed: /* listen to pad claimed.Device */ break;
            case SettingsChanged changed: Save(PadClaimSettingsJson.Write(changed.Settings)); break;
        }
    }
}
```

- **A disposed source cannot be started again.** Make a new `GodotPadSource` for the claimer instead. A claimer kept in an autoload can outlive many sources: a new source's `Start` skips the pads the claimer already lists, so the seat holder keeps the seat.
- **`Feed` returns true** for a joypad button or motion event that the seat does not accept: any pad but the seat's, a blocked button, or a button held through the claim until it is released. The host marks it handled and reads the rest.
- **Ask the claimer what the game may hear.** `AcceptsButton(device, button)` and `AcceptsAxis(device, axis)` are true only for the seat pad. `SeatDevice` is the seat pad's device index, or null.
- **A clock for hosts and tests.** `new GodotPadSource(claimer, () => clock)` stamps every event with a clock you supply, which must never go backwards. The default stamps `Time.GetTicksUsec()` as each event arrives, so times are quantised to the frame.
- **Settings.** Pass saved settings to the `PadClaimer` constructor, read with `PadClaimSettingsJson.TryRead`. The library never touches the file system: when the player's decisions change it raises `SettingsChanged`, and the host writes `PadClaimSettingsJson.Write(settings)` where it likes.
- **Host operations on the claimer:** `ClaimsOpen` (false refuses every claim), `ReleaseSeat`, `ForceClaim`, `Resample`, `SetVerdict` (block or trust a pad), `SetButtonBlocked` and `Forget`, each taking the time and returning the events it caused. Call them with the same clock the source uses and pass the events to your own handler.

## The claim gesture and the heuristics

- **Quiet window.** After a pad connects it must stay still for one second before it is offered. Button or axis activity restarts the window, and a pad that never settles within ten seconds is marked suspect. A button already down when the window ends marks the pad suspect (stuck).
- **Two-button claim.** A claim starts when a button is released after being held between 50 ms and 2 s. It completes when a different button, pressed within 3 s of the first release, is released after a hold in the same range. Pressing alone never claims, a single button repeated never claims, and axes never claim. A button held down when the claim completes is refused until it is released.
- **Suspect pads.** A button held for 10 s, a button that presses itself at a near-constant period (five presses within 60 s), a button released faster than 10 ms, or an axis that never stills marks the pad suspect. A suspect pad is never offered for a claim unless the host trusts it, and a claim in progress on it is cancelled. A pad that holds the seat runs no detectors.
- **Blocklist.** The host can block a whole pad or single buttons of it. Rules are keyed on the pad's identity from `get_joy_info`: vendor and product ids when both sides report them, the name when not, and the serial when both sides have one. A blocked button is ignored by every rule above.
- **Seat stealing.** One pad holds the seat. Another pad that completes the gesture takes it only when the holder has been idle for 2 s; otherwise the claim is refused as `SeatBusy`.
- **Restore on reconnect.** When the seat holder disconnects and a pad connects afterwards, the seat comes back to it once it has settled, if it is the only connected pad matching the holder's identity and the seat is still vacant.
- **Unlisted pads.** A device that sends input without ever being seen to connect, such as a pad injected by a test tool, is ignored unless the claimer was created with `UnlistedDevices = Accept`. Enable it in debug builds only.

The thresholds are `ClaimTuning` properties; pass your own to the `PadClaimer` constructor. Their defaults are listed in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Driving it with godot-mcp

A pad injected by godot-mcp never appears in `Input.GetConnectedJoypads()` and sends no connection signal, so the host must accept unlisted pads (a debug build of the demo does). It claims by press, not tap: a tap lasts one frame, about 16.7 ms, which is under the 50 ms minimum hold. Press one button, wait at least 100 ms, release it, then do the same with a second button.

## HidHide

On Windows, HidHide hides real pads from programs it does not list. Before testing with real pads, allow the Godot executables (the editor and its console build, or your exported game) in HidHide, or Godot will see no pad.

## The demo and the smoke

- `pwsh ./padclaim.ps1 demo` builds `demo/` and imports it into Godot headless. Open `demo/` in the editor and run it to see every connected pad with its status and suspect reasons, the seat holder and the latest events. `$env:GODOT_PATH` names the Godot console executable.
- `pwsh ./padclaim.ps1 smoke` builds the demo, then runs `demo/Smoke/ClaimSmoke.tscn` headless. It injects button events on pads the engine never reported and exits non-zero unless one-frame taps and a repeated single button do not claim and a two-button gesture does.

[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) covers the toolchain and the other commands.

## Known issues

- `DeviceRule` equality compares the blocked-buttons array by reference, so two equal rules can compare unequal (DRIVE-25).
- A `DeviceSnapshot`'s `ToString` prints its suspect-buttons array as a type name (DRIVE-32).
- A suspect pad can raise a duplicate `DeviceChanged` (DRIVE-33).

## Documentation

[docs/README.md](docs/README.md) is the map of the docs and links the glossary.

## Licence

MIT. See [LICENSE](LICENSE).
