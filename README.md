# godot-pad-claim

Gamepad claiming for Godot 4 C# that noisy controllers cannot fool.

A player claims a gamepad by a deliberate two-button gesture. A device that sends input on its own cannot claim: flight-sim panels and button boxes with stuck or self-repeating buttons look like a pad pressing "Start" forever to most input code, and they take over the player's seat. This library watches each device, keeps the noisy ones out of the claim prompt and lets the host persist a blocklist.

## Status

0.1.0, unreleased, API not stable. career-driver is the first consumer.

Usage: coming with the first release.

## Requirements

- Godot 4.7 .NET
- .NET 10

## How it works

- **Quiet window.** After a pad connects it must stay still for about one second before it is offered. Activity during the window restarts it; a pad that never settles within ten seconds is marked suspect.
- **Release-to-claim.** A claim starts when a button is released after being held between 50 ms and 2 s. Pressing alone never claims, so a button that is always down cannot start one.
- **A different second button.** The claim completes when a different button, pressed within 3 s of the first release, is released after a hold between 50 ms and 2 s. One stuck or self-repeating button cannot produce that by chance.
- **Suspect devices are never offered.** A pad with a stuck, self-repeating or sub-human-speed button, or a jittery axis, is quarantined. Axes never claim.
- **A blocklist the host persists.** Rules are kept per device and per button, keyed on vendor and product IDs, with the name as a fallback. The library never touches the file system: it hands the host settings to store and takes them back as JSON.

## Documentation

[docs/README.md](docs/README.md) is the map of the docs and links the glossary.

## Licence

MIT. See [LICENSE](LICENSE).
