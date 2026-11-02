# Glossary

Terms the docs use in a project-specific sense, in alphabetical order.

- **Blocklist**: the per-device and per-button rules the host persists; a blocked device is never offered for a claim, and a blocked button is ignored by the state machine.
- **Candidate button**: the button whose press and release, held within the human range, started a claim and now waits for a confirm button.
- **Claim**: a player taking a gamepad for the seat by a deliberate gesture, a release of one button then a press and release of a different one within the confirm window.
- **Confirm button**: the different second button whose press and release within 3 s of the candidate's release completes a claim.
- **Gate**: `tools/gate.ps1`, the wrapper every build, test and format runs through, which logs the output, applies a ceiling and kills a stalled run.
- **Held-through-claim button**: a button already down when a claim completes; it is refused until it is released.
- **Quiet window**: the stillness a device needs after connecting, 1 s by default, before it is offered; button or axis activity restarts it and a device that never settles is suspect.
- **Seat**: the single slot a claimed pad holds; it is vacant or held by one device, and a different pad takes it only once the holder has been idle for 2 s.
- **Suspect device**: a device with a stuck, self-repeating or sub-human-speed button or a jittery axis; it is never offered for a claim unless the host trusts it.
- **Trusted device**: a device whose rule says the host vouches for it, so noise detectors are off for it.
- **Unlisted device**: a device that sends input without ever being seen to connect, such as a pad injected by a test tool; ignored unless the host sets `UnlistedDevices = Accept`.
