# 0016 - The `bye` field goes into wire format v1

**Status:** Accepted - 2026-09-27
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
- Sent on graceful exit and on `WM_ENDSESSION` - never on the cancellable query phase -
  three times about 50 ms apart, since UDP offers no retry and there will be no further
  heartbeat. A sender claiming on its way out broadcasts its state datagram first.
- A receiver that accepts a `bye` **bypasses §5.4 entirely**: the `seq` and `activeOwner`
  it carries are ignored in both directions of ordering, so a departure can never move the
  latch. §5.2 holds by receiver enforcement, not by sender discipline.
- It **clears peer presence immediately**, without waiting out the presence window, and
  sets a departed flag. Presence is re-established only by the next accepted non-`bye`
  datagram.
- It **does not clear the §7.6 quarantine observation latch**.
- It runs the full §7.1 ingress pipeline. A `bye` that fails `pairId`, `mac`, roster, or
  bound checks is dropped like anything else.

## Consequences

- The §9.2-5 window collapses to near-zero for deliberate shutdown, which is the case that
  actually happens daily.
- It **narrows** the risk rather than closing it. Power loss, a crash, or a laptop carried
  out of range still fall back to the 10 s timeout. §9.2-5 stays on the risk register.
- Forgery is not a realistic threat: every datagram is HMAC-SHA256 keyed by `pairKey`,
  which never crosses the network. A **replayed** `bye` needs no key, and §7.1 already
  accepts replay explicitly. With the three receipt rules above, its only effect is an
  unmute, which is Goal 1's safe direction. No anti-replay rule is specified.
- It improves diagnosability more than expected: a clean `bye` and a presence timeout are
  now distinguishable in the log, which is what makes manual matrix row G9 - telling a
  wire-version mismatch apart from a switched-off peer - answerable at all. See
  [0015](0015-local-rolling-log.md).
- It edits a reviewed design document. The change is confined to §5.4, the §7.1 payload
  table, §7.6, and §10; no decision in the design is reversed.

## History - the semantics were wrong before they were right

This ADR shipped in revision 3 with the timing argument correct and the **semantics
wrong**. The adversarial review recorded in [`../review-2026-09-28.md`](../review-2026-09-28.md)
found two Critical defects, both fixed in `design.md` revision 4:

- **B-1.** "A `bye` never alters `activeOwner`" was sender discipline that the receiver
  pipeline did not enforce. Ingress sent every accepted datagram to §5.4, and a `bye`
  carries `seq` and `activeOwner` like any other, so a departing machine holding the latest
  `seq` moved the latch on its way out. The fix is the bypass rule above.
- **B-2.** "A forged `bye` causes an unmute ... the safe direction" was premature. Presence
  is also quarantine's expire-versus-adopt input, so a `bye` could clear an observation
  that had already legitimately happened, re-opening the lid-open defect §7.6 exists to
  prevent - with no attacker involved. The fix is the observation latch in §7.6.

Recorded rather than quietly rewritten, per `README.md`'s rule in this directory. The
lesson generalises: this decision was reasoned carefully about *when* to make a change and
not at all about how the receiver would behave, and a wire field is exactly the place where
that asymmetry is expensive.

## Reversibility

The behaviour is reversible cheaply - stop sending, ignore on receipt. The **field** is
not: once v1 is deployed to both machines, removing it is a version bump requiring a
simultaneous update. That asymmetry is the entire reason it is being added now rather than
when it is next convenient.
