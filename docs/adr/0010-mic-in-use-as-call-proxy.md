# 0010 — Mic-in-use is the proxy for in-a-call

**Status:** Accepted · 2026-09-27
**Source:** `design.md` §9.1, decision D-3

Restated here so that all project decisions live in one place. The design remains
normative; if the two ever disagree, `design.md` wins.

## Context

The design needs to know when a machine is in a call. `design.md` §3 rules out any
integration with Teams, Zoom, or Slack APIs, so detection must be OS-level and
app-agnostic. "At least one capture session in `AudioSessionStateActive`" is the available
signal.

## Decision

Accept "microphone in use" as the proxy for "in a call".

## Consequences

- Detection works for every meeting application without per-app work, and keeps working
  when one of them changes its API.
- A participant muted *inside* the meeting app still holds the capture session, so they
  remain protected by the §5.5 safety override. This is the common case and it works.
- A participant who has fully released the microphone is not protected. They are a
  listener at that point, so the cost is bounded.
- Non-call microphone use — dictation, voice assistants, a notification chime — reads as a
  call. The denylist and the claim-edge debounce exist to narrow this, and
  [0009](0009-safety-signal-is-not-debounced.md) accepts that the safety signal stays
  deliberately broad.
- **It rests on an unverified assumption.** §9.2-3 notes that muting the render endpoint
  is assumed not to disturb capture sessions, and that the assumption is load-bearing for
  the entire auto-claim loop. A standalone spike before phase 1 was considered and
  declined; it is verified instead against the running app in phase 2, via manual matrix
  row H5, which is why that row is run first among the phase-2 rows. The risk of finding
  out late is accepted knowingly — see `implementation-plan.md` §4.7.

## Reversibility

Yes, but only by adding meeting-app integration, which §3 currently rules out as a
non-goal. Reversing this is therefore a scope change rather than a code change.
