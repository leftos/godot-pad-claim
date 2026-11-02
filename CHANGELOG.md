# Changelog

All notable changes to this project are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Unreleased

The first version, 0.1.0.

- A pad claims the seat by releasing one button after a human hold, then a different button within 3 s.
- A pad must stay still for a quiet window of one second after connecting before it is offered.
- A pad with a stuck, self-repeating or sub-human-speed button, or an axis that never settles, is marked suspect and never offered.
- The seat has one holder, which another pad can take once the holder has been idle for 2 s.
- A seat holder that disconnects gets the seat back by itself when it returns, settles and is the only matching pad.
- The host can block or trust a pad, block single buttons, forget a pad, re-sample a suspect one, force a claim and close claims.
- Pads are identified by vendor and product ids from Godot's joypad info, else by name, and by serial when both have one.
- Settings round-trip through versioned JSON that the host saves on `SettingsChanged`; the library never touches the file system.
- Every threshold is a `ClaimTuning` property with a default.
- `GodotPadSource` feeds Godot's joypad input to the engine-free core and tells the host which events to swallow.
- Pads injected by test tools such as godot-mcp are ignored unless the host sets `UnlistedDevices = Accept`.
- A demo project shows every pad, the seat and the events, and `padclaim.ps1 smoke` proves the claim headless in the real engine.
