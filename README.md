# solo-speaker

Two machines, one voice.

`solo-speaker` coordinates global audio mute between a desktop and a laptop that sit on the
same desk. When both are in proximity, exactly one is audible. When they're apart — laptop
off, laptop elsewhere, or you're working away from the desk — both are audible.

## The problem

Meetings get joined on whichever machine is convenient, and which one that is changes
through the day. With both machines awake on the same desk, notification sounds, ringtones,
and duplicated meeting audio overlap. Muting by hand every time means remembering to unmute
by hand every time, which nobody does.

## How it decides

A single replicated latch, `(activeOwner, seq)`, held identically on both machines. It moves
on exactly two events:

- a call starts on a machine — detected by the microphone going live
- a manual claim — global hotkey, tray click, or unmuting from the volume flyout

Nothing else moves it. Not idle time, not focus, not lock state, and **not a call ending** —
if you take a call on the laptop, the laptop stays the active machine afterwards.

A machine mutes only when it can affirmatively name its peer as the owner *and* isn't
capturing audio itself. Anything ambiguous — an unknown owner, a missing peer, a live
microphone — leaves it audible. Silence is never the fallback.

## Status

Pre-implementation. The design is reviewed and settled; no code yet.

| Phase | Scope | State |
|---|---|---|
| 1 | Foundation + manual claim: roster, wire format, pairing, ledger, tray, hotkey | Not started |
| 2 | Call detection via WASAPI capture sessions | Not started |
| 3 | Bluetooth RSSI proximity, if LAN presence proves too coarse | Deferred |

Phase 1 deliberately ships without call detection, so a manual claim can mute a machine
that's mid-meeting. That limitation is accepted and recorded, not overlooked.

## Design

[`docs/design.md`](docs/design.md) is the full design, at revision 2. It went through an
adversarial review that found four Critical defects in revision 1 — the mute predicate
never consulted microphone state, `activeOwner` had an unbounded domain that could mute
both machines durably, the HMAC was keyed by a secret broadcast in cleartext, and the phase
boundary was drawn through the wire format. All four are fixed in revision 2, and §9.1
records three open decisions that remain reversible.

The rejected alternative — picking the active machine by most-recent input — is documented
in §4 so it doesn't get proposed again.

## Platform

Windows only. Core Audio (WASAPI) for both mute actuation and microphone detection, UDP
broadcast on the local subnet for peer coordination. No cloud service, no account, no
broker.
