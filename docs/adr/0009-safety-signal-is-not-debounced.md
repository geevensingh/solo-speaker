# 0009 — The safety signal is filtered but not debounced

**Status:** Accepted · 2026-09-27
**Source:** `design.md` §9.1, decision D-2

Restated here so that all project decisions live in one place. The design remains
normative; if the two ever disagree, `design.md` wins.

## Context

Revision 1 derived a single debounced microphone signal and used it only to fire ownership
claims. That was wrong in both directions at once: the debounce guaranteed an audio outage
at the start of every call, and was simultaneously too short to outlast a pre-join screen.

The signal has two consumers that want opposite biases. The §5.5 safety override wants
breadth and speed, because a false positive costs a brief both-audible window. The §5.1
claim edge wants narrowness and patience, because a false positive moves a latch that does
not move back.

## Decision

Two signals from one mechanism:

| | `selfMicLive` (safety) | Claim edge (ownership) |
|---|---|---|
| Denylist | Applied | Applied |
| Debounce | **Not applied** | Applied, 5 s default |
| Bias | Broad and fast | Narrow and slow |
| Cost of being wrong | Both audible, briefly | Nothing; the latch does not move |

## Consequences

- The start-of-call audio outage disappears: the machine becomes audible the instant the
  mic opens, and the ownership write follows once the session has proven durable.
- A pre-join screen the user abandons makes that machine audible for as long as they sit
  on it, but never moves ownership. That is the intended trade.
- It creates the failure mode with the worst blast radius in the design: an always-on mic
  consumer keeps `selfMicLive` true forever, making the machine permanently unmutable,
  silently. §7.4's denylist discovery UI is the mitigation and §9.2-2 the standing risk.
- The override only ever *relaxes* muting. It can never cause a mute that would not
  otherwise occur, so the stickiness property of §5.2 is untouched.

## Reversibility

Yes. Both biases are configuration of one mechanism.
