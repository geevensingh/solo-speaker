# 0016 — The `bye` field goes into wire format v1

**Status:** Accepted · 2026-09-27
**Amends:** `design.md` §7.1 and §7.6
**Resolves:** `implementation-plan.md` §8.6

## Context

`design.md` §9.2-5 records an asymmetry and declines to fix it: muting is immediate, but
restoring audio after peer loss takes up to the 10 s presence window. The design's own
words are that "silence therefore arrives fast and leaves slowly, which is the wrong way
round given Goal 1. Bounded, not fixed."

The common case that produces this is entirely benign: you deliberately shut a machine
down, and the other one sits muted for up to ten seconds afterwards.

This was initially filed as an optional improvement. That was a misreading. §8 of the
design freezes the wire format in phase 1 precisely so that nothing on the wire becomes a
two-machine migration, which makes this a **now-or-never** decision rather than a
nice-to-have: added later, it costs exactly the coordinated update the phase re-cut exists
to prevent.

## Decision

Add a `bye` boolean to wire format v1, between `micLive` and `sentUtc`. Always emitted
explicitly, like `micLive`, and absent means version mismatch rather than `false`.

- `bye: true` means "I am leaving deliberately."
- On graceful exit — including logoff and shutdown — a machine sends its final datagram
  with `bye: true`, three times about 50 ms apart, since UDP offers no retry and there
  will be no further heartbeat.
- A receiver that accepts a `bye` clears peer presence **immediately**, without waiting out
  the presence window.
- A `bye` **never alters `activeOwner`**. §5.2 is explicit that a peer disappearing changes
  mute output but never ownership, and this is a peer disappearing with better manners, not
  a new kind of event.
- It runs the full §7.1 ingress pipeline. A `bye` that fails `pairId`, `mac`, roster, or
  bound checks is dropped like anything else.

## Consequences

- The §9.2-5 window collapses to near-zero for deliberate shutdown, which is the case that
  actually happens daily.
- It **narrows** the risk rather than closing it. Power loss, a crash, or a laptop carried
  out of range still fall back to the 10 s timeout. §9.2-5 stays on the risk register.
- A forged `bye` causes an unmute. That is the safe direction by Goal 1, and no worse than
  the datagram replay §7.1 already accepts as within tolerance.
- It improves diagnosability more than expected: a clean `bye` and a presence timeout are
  now distinguishable in the log, which is what makes manual matrix row G7 — telling a
  wire-version mismatch apart from a switched-off peer — answerable at all. See
  [0015](0015-local-rolling-log.md).
- It edits a reviewed design document. The change is confined to the §7.1 payload table and
  a row in §7.6's unmute paths; no decision in the design is reversed.

## Reversibility

The behaviour is reversible cheaply — stop sending, ignore on receipt. The **field** is
not: once v1 is deployed to both machines, removing it is a version bump requiring a
simultaneous update. That asymmetry is the entire reason it is being added now rather than
when it is next convenient.
