# Glossary

Terms the docs use in a project-specific sense, in alphabetical order.

- **Blocklist**: the per-device and per-button rules the host persists; a blocked device is never offered for a claim, and a blocked button is ignored by the state machine.
- **Candidate button**: the button whose press and release, held within the human range, started a claim and now waits for a confirm button.
- **Claim**: a player taking a gamepad for the seat by a deliberate gesture, a release of one button, then a different one pressed within the confirm window and released.
- **Confirm button**: the different second button that completes a claim: pressed within 3 s of the candidate's release, then released after a hold within the human range.
- **Gate**: `tools/gate.ps1`, the wrapper every build, test and format runs through, which logs the output, applies a ceiling and kills a stalled run.
- **Held-through-claim button**: a button already down when a claim completes; it is refused until it is released.
- **Host**: the game or tool that uses the library: it owns the nodes, feeds input to `GodotPadSource`, saves the settings and calls the claimer's operations.
- **Phantom pad**: a virtual or software device that reports a pad and presses buttons like a hand; the heuristics cannot tell it from a player's pad, so only the blocklist stops it.
- **Quiet window**: the stillness a device needs after connecting, 1 s by default, before it is offered; button or axis activity restarts it and a device that never settles is suspect.
- **Restore**: the seat going back by itself to a holder that disconnected, once a pad that connected afterwards has settled, is the only connected match for the holder's identity and finds the seat vacant.
- **Seat**: the single slot a claimed pad holds; it is vacant or held by one device, and a different pad takes it only once the holder has been idle for 2 s.
- **Smoke**: `padclaim.ps1 smoke`, a headless Godot run that injects button events and exits non-zero unless the claim gesture behaves as designed.
- **Suspect device**: a device with a stuck, self-repeating or sub-human-speed button or a jittery axis; it is never offered for a claim unless the host trusts it.
- **Trusted device**: a device whose rule says the host vouches for it, so noise detectors are off for it.
- **Unlisted device**: a device that sends input without ever being seen to connect, such as a pad injected by a test tool; ignored unless the host sets `UnlistedDevices = Accept`.
