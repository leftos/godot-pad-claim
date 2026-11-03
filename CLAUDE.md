# godot-pad-claim

A C# library for Godot 4 that lets a player claim a gamepad by a deliberate gesture and keeps noisy devices from claiming. This file is a router.

## Start here

- `docs/README.md`: the map of the docs and the glossary.
- `docs/ARCHITECTURE.md`: the layers, the Task Index and the integration footguns.
- `docs/DEVELOPMENT.md`: the toolchain, the commands, the hooks and how tests run.

## Rules

- The core `src/PadClaim` and its tests `tests/PadClaim.Tests` stay free of any Godot reference.
- The repo is public: no private paths, machine names or secrets in any file.
- Every build and test runs through `padclaim.ps1`, which runs it under the gate (`tools/gate.ps1`).
- Test names are PascalCase with no underscores.
