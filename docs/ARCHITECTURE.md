# godot-pad-claim — architecture

A library that decides which gamepad a player has claimed. Layers: `PadClaim` (an engine-free core), `PadClaim.GodotInput` (a thin Godot adapter, planned) and `PadClaim.Tests`, plus a `demo/` Godot project (planned). The core references nothing from Godot and never reads a clock: it is fed timestamped samples and returns events, so every rule is tested without an engine. Terms used in a project sense are in [glossary.md](glossary.md).

## Task Index

"I need to change X, which files?" One row per kind of change, files in the order to read them.

| Task | Files, in order | Deep doc |
|---|---|---|
| Change a threshold (quiet window, hold range, confirm window, repeat detection) | `src/PadClaim/ClaimTuning.cs` → `tests/PadClaim.Tests/ClaimTuningTests.cs`, `tests/PadClaim.Tests/ClaimGestureTests.cs`, `NoiseTests.cs` | none yet |
| Change how two connections of a device are matched | `src/PadClaim/DeviceIdentity.cs` → `tests/PadClaim.Tests/IdentityTests.cs` | none yet |
| Change the settings file format | `src/PadClaim/PadClaimSettingsJson.cs` → `tests/PadClaim.Tests/SettingsJsonTests.cs` | none yet |
| Change the claim gesture | `src/PadClaim/DeviceTracker.cs` → `src/PadClaim/PadClaimer.cs` → `tests/PadClaim.Tests/ClaimGestureTests.cs` | none yet |

## Layers

- **`PadClaim`** (`src/PadClaim/`): the engine-free core. Owns the claim state machine fed timestamped samples, the devices, the seat and the settings records. References the .NET framework only; must never reference Godot.
- **`PadClaim.GodotInput`** (`src/PadClaim.GodotInput/`, planned): a thin adapter. Reads `Input` and `joy_connection_changed`, stamps each event with the time it arrives and passes values to the core as `int`, `string` and `TimeSpan`. May reference Godot and `PadClaim`; declares no `GodotObject` subclass.
- **`PadClaim.Tests`** (`tests/PadClaim.Tests/`): engine-free tests of the core. References `PadClaim` only; `ReferenceTests` pins that neither it nor the core references an assembly whose name starts with `Godot`.
- **`demo/`** (planned): a Godot project that hosts the adapter and shows every device with its status.

## Integration Footguns

A change to A that needs a change to B, where nothing but a test or a reviewer catches the miss.

- **Name the adapter namespace `PadClaim.Godot`** → inside it, `Godot.Input` binds to `PadClaim.Godot` and the engine types stop resolving. Use `PadClaim.GodotInput`.
- **Add a `GodotObject` or `Node` subclass to the adapter** → it then needs script registration and runs source generators. The adapter declares none and the host owns the nodes.
- **Subscribe the static `Input.JoyConnectionChanged` in the adapter** → also remove the handler in `Dispose`. A static subscription left behind stops the editor unloading the assembly on rebuild.
- **Add a branch to the claim state machine** → keep CA1502 at 8: dispatch by state to small methods rather than growing one method.
- **Assert on a record holding `ImmutableArray`** → the record compares the array by reference, so two equal records are unequal. Tests compare fields.

## Test locations

- `tests/PadClaim.Tests/ReferenceTests.cs`: pins from assembly metadata that the core and the tests reference no Godot assembly.
- `tests/PadClaim.Tests/`: every other test of the core goes here, one class per concern, with names in PascalCase and no underscores.

## Deep docs

None yet.
