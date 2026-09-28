# 0008 — An external unmute is a manual claim

**Status:** Accepted · 2026-09-27
**Source:** `design.md` §9.1, decision D-1

Restated here so that all project decisions live in one place. The design remains
normative; if the two ever disagree, `design.md` wins.

## Context

When a machine goes unexpectedly silent, the natural human reflex is to reach for the
volume flyout and unmute it. Revision 1 of the design treated that as drift and corrected
it on the next reconcile tick, so the reflex failed — the machine re-muted itself under
the user's hand.

## Decision

An external unmute of an endpoint *we muted* is treated as a **manual claim** (§7.4), not
as drift to be corrected. It becomes the third input surface alongside the global hotkey
and the tray left-click.

## Consequences

- The most natural recovery gesture now works, and works in the way the user intends:
  the machine becomes audible and stays audible.
- It widens §5.1's writer set in spirit, if not in letter. The design flags this itself as
  the decision most worth a second look.
- It invites a self-feedback loop, since the actuator's own `SetMute` raises the same
  change notification. §7.3 addresses this by comparing against the last value written and
  ignoring notifications within 250 ms of our own write. Both halves of that — suppression
  inside the window, and *honouring* a change outside it — are tested
  (`implementation-plan.md` §4.6).

## Reversibility

Yes, cheaply. The design rates it so, and the mechanism is confined to the reconciler.
