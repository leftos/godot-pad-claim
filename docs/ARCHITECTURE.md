# godot-pad-claim — architecture

A library that decides which gamepad a player has claimed. Layers: `PadClaim` (an engine-free core), `PadClaim.GodotInput` (a thin Godot adapter) and `PadClaim.Tests`, plus a `demo/` Godot project. The core references nothing from Godot and never reads a clock: it is fed timestamped samples and returns events, so every rule is tested without an engine. Terms used in a project sense are in [glossary.md](glossary.md).

## Task Index

"I need to change X, which files?" One row per kind of change, files in the order to read them.

| Task | Files, in order | Deep doc |
|---|---|---|
| Change a threshold (quiet window, hold range, confirm window, repeat detection) | `src/PadClaim/ClaimTuning.cs` → `tests/PadClaim.Tests/ClaimTuningTests.cs`, `tests/PadClaim.Tests/ClaimGestureTests.cs`, `NoiseTests.cs` | none yet |
| Change how two connections of a device are matched | `src/PadClaim/DeviceIdentity.cs` → `tests/PadClaim.Tests/IdentityTests.cs` | none yet |
| Change the settings file format | `src/PadClaim/PadClaimSettingsJson.cs` → `tests/PadClaim.Tests/SettingsJsonTests.cs` | none yet |
| Change the claim gesture | `src/PadClaim/DeviceTracker.cs` → `src/PadClaim/PadClaimer.cs` → `tests/PadClaim.Tests/ClaimGestureTests.cs` | none yet |
| Change how a suspect pad is detected (stuck, repeating, sub-human, jittery axis) | `src/PadClaim/NoiseDetector.cs` → `src/PadClaim/DeviceTracker.cs` → `tests/PadClaim.Tests/NoiseTests.cs` | none yet |
| Add or change an event the host receives | `src/PadClaim/ClaimEvents.cs` → the code that returns it in `src/PadClaim/PadClaimer.cs`, `DeviceTracker.cs` or `Seat.cs` → `src/PadClaim.GodotInput/GodotPadSource.cs` only if it reads the event | none yet |
| Change how Godot's input reaches the core (connect scan, stamping, which events `Feed` swallows) | `src/PadClaim.GodotInput/GodotPadSource.cs` → `demo/Smoke/ClaimSmoke.cs` | none yet |
| Change the demo or the headless smoke | `demo/Main.cs`, `demo/Smoke/ClaimSmoke.cs` → `padclaim.ps1` (`demo` and `smoke` verbs) | [DEVELOPMENT.md](DEVELOPMENT.md) |
| Change the seat (stealing from an idle holder, restore on reconnect, closed claims, release, accepted input) | `src/PadClaim/Seat.cs` → `src/PadClaim/PadClaimer.cs` → `tests/PadClaim.Tests/SeatTests.cs` | none yet |
| Change the settings operations (block or trust a device, block a button, forget, re-sample, force a claim, unlisted devices) | `src/PadClaim/PadClaimer.cs` → `src/PadClaim/DeviceTracker.cs` → `src/PadClaim/Seat.cs` → `tests/PadClaim.Tests/SettingsTests.cs` | none yet |

## Layers

- **`PadClaim`** (`src/PadClaim/`): the engine-free core. Owns the claim state machine fed timestamped samples, the devices, the seat and the settings records. References the .NET framework only; must never reference Godot.
- **`PadClaim.GodotInput`** (`src/PadClaim.GodotInput/`): a thin adapter, `GodotPadSource`. Reads `Input` and `joy_connection_changed`, stamps each call with the time it runs (`Time.GetTicksUsec()`, or a clock the host passes to the second constructor, which the headless smoke drives by hand) and passes values to the core as `int`, `string` and `TimeSpan`. May reference Godot and `PadClaim`; declares no `GodotObject` subclass.
- **`PadClaim.Tests`** (`tests/PadClaim.Tests/`): engine-free tests of the core. References `PadClaim` only; `ReferenceTests` pins that neither it nor the core references an assembly whose name starts with `Godot`.
- **`demo/`**: a Godot project that hosts the adapter and shows every device with its status, the seat and the latest events; built with `padclaim.ps1 demo`. A debug build accepts unlisted devices, so a pad injected by godot-mcp can claim.

## Public API

Times are `TimeSpan` since an origin the host supplies; the core never reads a clock. `device` is Godot's device index for one connection.

**Core, `PadClaim`:**

- **`PadClaimer`**: built from `PadClaimSettings`, optional `ClaimOptions` and optional `ClaimTuning`. The sample entry points `Connect`, `Disconnect`, `Button`, `Axis` and `Tick` each return the `ClaimEvent`s they caused, in order. The host operations are `ReleaseSeat`, `ForceClaim`, `Resample`, `SetVerdict`, `SetButtonBlocked` and `Forget`, plus the `ClaimsOpen` switch. The reads are `Settings`, `Devices`, `SeatDevice`, `AcceptsButton`, `AcceptsAxis` and `RestOf`.
- **`ClaimOptions`**: `RequireKnownGamepad` (default true: a device without an engine gamepad layout is `NotAGamepad` unless trusted) and `UnlistedDevices` (`Ignore` by default, or `Accept`).
- **`DeviceIdentity`**: vendor id, product id, name and serial, read from `get_joy_info` by `FromJoyInfo`. `Matches` compares ids when both sides report both, else names, and serials only when both sides have one.
- **Settings**: `PadClaimSettings` holds one `DeviceRule` per pad (identity, `DeviceVerdict` of `None`, `Blocked` or `Trusted`, and the blocked buttons). `PadClaimSettingsJson.Write` and `TryRead` turn it into and out of versioned JSON.
- **`DeviceSnapshot`**: a connected pad's `Device`, `Identity`, `Status` (`DeviceStatus`), `Reasons` (the flags of `SuspectReason`), `SuspectButtons`, `Trusted` and `Unlisted`.
- **Events**: `DeviceAdded`, `DeviceChanged`, `DeviceRemoved`, `ClaimStarted`, `ClaimCancelled` (`CancelReason`), `SeatClaimed`, `SeatMoved`, `SeatRestored`, `ClaimRefused` (`RefuseReason`), `SeatLost` (`SeatLossReason`) and `SettingsChanged`, all records under the abstract `ClaimEvent`.

**Adapter, `PadClaim.GodotInput`:** `GodotPadSource` takes a `PadClaimer` and, optionally, a clock. `Raised` carries every event; `Start` connects the pads already present and subscribes `Input.JoyConnectionChanged`; `Feed` takes an `InputEvent` from the host's `_Input` and returns true when the host should mark it handled; `Tick` runs from `_Process`; `Dispose` unsubscribes. A source is started once and lives as long as its claimer.

## Claim state machine

A device is in one `DeviceStatus`:

- **Connect** puts a pad in `Blocked` when its rule says so, in `NotAGamepad` when it has no gamepad layout and the host requires one, and in `Settling` otherwise. An unlisted pad is admitted straight to `Ready` with no quiet window when `UnlistedDevices` is `Accept`.
- **`Settling`** restarts its stillness clock on any button change or on an axis leaving its reference by more than `AxisStillBand`. `Tick` moves it to `Ready` after `QuietWindow` of stillness, or to `Suspect` when a button is still down (stuck) or when `SettleLimit` runs out first (never settled, with the jittery axis or the restarting buttons named).
- **`Ready`** moves to `AwaitingConfirm` when a button is released after a hold in [`MinClaimHold`, `MaxClaimHold`]; a release outside the range, and every axis event, change nothing.
- **`AwaitingConfirm`**: a release of a different button, pressed after the first release and within `ConfirmWindow` of it, after a human hold, is a claim attempt. The same button again re-arms the claim, a chord or a late press makes the new button the candidate, and `Tick` past `ConfirmWindow` cancels the claim with `Timeout`.
- **A claim attempt** becomes `Claimed` when the seat is vacant (`SeatClaimed`) or its holder has been idle for `SeatStealIdle` (`SeatMoved`, the old holder going to `Ready`). Otherwise the pad returns to `Ready` with `ClaimRefused`: `SeatBusy`, or `ClaimsClosed` when the host has closed claims.
- **`Suspect`** is entered from `Settling`, `Ready` or `AwaitingConfirm` when a detector fires, and cancels a claim in progress. It is left through `SetVerdict(Trusted)`, `Resample` or a reconnect, each of which settles the pad afresh. A `Claimed` pad runs no detectors.
- **Disconnect** removes the pad and, for the seat holder, vacates the seat and remembers its identity. When a pad connecting afterwards settles, matches that identity as the only connected match and the seat is still vacant, it gets the seat back (`SeatRestored`).
- **Host operations**: `ReleaseSeat` returns the holder to `Ready`; `SetVerdict(Blocked)` makes matching pads `Blocked` and the holder loses the seat; `SetVerdict(Trusted)` and `SetVerdict(None)` send a suspect or blocked pad back to `Settling`; `ForceClaim` takes the seat unconditionally. A change of rule raises `SettingsChanged`.

The seat is vacant or held by one device with the time of its last activity: the holder's button changes and its axis moves beyond rest. A button down at the moment of a claim is refused by `AcceptsButton` until it is released. Blocked buttons are dropped before the state machine sees them.

## Thresholds

`ClaimTuning` in `src/PadClaim/ClaimTuning.cs` holds every threshold as an init property; these are the defaults.

| Property | Default | Meaning |
|---|---|---|
| `QuietWindow` | 1 s | stillness a pad needs after connecting before it is offered |
| `SettleLimit` | 10 s | how long the quiet window may keep restarting before the pad is suspect |
| `AxisStillBand` | 0.05 | how far an axis may move and still count as still |
| `MinClaimHold` | 50 ms | shortest press that can start or confirm a claim |
| `MaxClaimHold` | 2 s | longest press that can start or confirm a claim |
| `SubHumanHold` | 10 ms | a release sooner than this after its press marks the pad suspect |
| `StuckHold` | 10 s | a button down this long marks the pad suspect |
| `ConfirmWindow` | 3 s | from the first button's release to the second button's press |
| `RepeatPresses` | 5 | presses of one button judged for a constant period |
| `RepeatJitterFloor` | 20 ms | the least deviation from the mean interval still counted as periodic |
| `RepeatJitterFraction` | 0.04 | the same bound as a fraction of the mean interval; the larger of the two applies |
| `RepeatHorizon` | 60 s | the judged presses must fall within this span |
| `SeatStealIdle` | 2 s | idle time after which another pad may take the seat |

## Residual risk

The heuristics reject a device that behaves unlike a hand, not one that behaves like a hand.

- **A phantom pad that behaves like a human**: a virtual or software device that sends well-spaced, human-length presses of two different buttons passes the quiet window and the gesture, and claims a vacant seat. Only the host's blocklist stops it.
- **A holder that goes idle**: after `SeatStealIdle` of no input the seat holder can lose the seat to a noisy pad that passes the gesture, for example two self-pulsing buttons on one device, with human-length holds, a period longer than the quiet window and a phase offset under `ConfirmWindow`, that complete a claim before five presses are there to judge as periodic. The defences are the blocklist and `ReleaseSeat`.

## Integration Footguns

A change to A that needs a change to B, where nothing but a test or a reviewer catches the miss.

- **Name the adapter namespace `PadClaim.Godot`** → inside it, `Godot.Input` binds to `PadClaim.Godot` and the engine types stop resolving. Use `PadClaim.GodotInput`.
- **Add a `GodotObject` or `Node` subclass to the adapter** → it then needs script registration and runs source generators. The adapter declares none and the host owns the nodes.
- **Subscribe the static `Input.JoyConnectionChanged` in the adapter** → also remove the handler in `Dispose`. A static subscription left behind stops the editor unloading the assembly on rebuild.
- **Connect a pad from `Start`'s scan of `Input.GetConnectedJoypads()`** → Godot emits `joy_connection_changed` deferred but updates the connected list at once, so the same pad's signal arrives after the scan. `GodotPadSource` keeps the set of devices it has connected and skips a second connect.
- **Add a branch to the claim state machine** → keep CA1502 at 8: dispatch by state to small methods rather than growing one method.
- **Assert on a record holding `ImmutableArray`** → the record compares the array by reference, so two equal records are unequal. Tests compare fields.

## Test locations

- `tests/PadClaim.Tests/ReferenceTests.cs`: pins from assembly metadata that the core and the tests reference no Godot assembly.
- `tests/PadClaim.Tests/`: every other test of the core goes here, one class per concern (`IdentityTests`, `SettingsJsonTests`, `ClaimTuningTests`, `ClaimGestureTests`, `NoiseTests`, `SeatTests`, `SettingsTests`), with names in PascalCase and no underscores.
- `demo/`: a Godot project that hosts the adapter, for running by hand against real or injected pads (`padclaim.ps1 demo` builds it).
- `demo/Smoke/ClaimSmoke.cs`: the headless smoke (`padclaim.ps1 smoke`). It drives the adapter in the real engine on a clock it moves itself: it checks that injected events reach `_Input`, that one-frame taps and a repeated single button do not claim, and that a two-button gesture takes the seat. It exits 0 on success, 1 on a failed scenario, 2 on its watchdog and 3 when injected events do not reach `_Input`.

## Deep docs

None yet.
