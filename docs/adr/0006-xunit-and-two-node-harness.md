# 0006 - xUnit, plus a two-node in-process harness

**Status:** Accepted - 2026-09-27

## Context

`design.md` §10 splits its test plan into unit tests against the pure reducer and an
"integration (two-machine manual matrix)". That split is drawn in the wrong place.

Several rows filed as manual - claim ping-pong, concurrent equal-`seq` convergence, stale
`seq`, quarantine adoption on lid-open - do not actually need two machines. They need two
reducers, a controllable clock, and a transport that can misbehave on demand. Leaving them
in the manual matrix means they are exercised a handful of times by hand instead of on
every commit, and they cover the convergence logic where a regression is least visible.

## Decision

xUnit for the test framework, and a **two-node in-process harness** in
`SoloSpeaker.Core.Tests`: two reducers, two state stores over temp directories, a shared
test-controlled `IClock`, and a fake `IPeerTransport` that can drop, duplicate, reorder,
and delay datagrams.

Validation - HMAC, roster checks, the `seq` bound - sits *above* the transport seam, so
hostile-input tests run against real bytes without a socket.

## Consequences

- Convergence, quarantine, packet loss, replay, and simultaneous-rejoin behaviour become
  deterministic automated tests rather than manual rows.
- The `IClock` seam becomes mandatory, not optional. Nothing outside the composition root
  may read the system clock; a single real-clock call in the reducer makes these tests
  slow or flaky.
- The manual matrix shrinks to what genuinely requires two machines and real hardware,
  which makes it short enough to actually run every release. Rows C3 and A7 keep only their
  real-hardware halves as of work item 4, and F8's expectation was rewritten once the
  harness forced the question of what "bounded" meant - see `design.md` §9.2-8.
- The harness's transport is a **broadcast medium**, not a point-to-point pipe: every send
  reaches both subscribers including the sender, because that is what an IPv4 subnet
  broadcast does. A pipe would make `IngressResult.SelfOrigin` unreachable inside the loop
  and leave the permanent-mute failure §7.1 exists to prevent structurally untestable.
- The harness is code that must itself be correct. A fake transport that is too forgiving
  produces tests that pass for the wrong reason, so its misbehaviour modes are explicit
  and opt-in per test rather than implicit defaults.
- xUnit specifically: the default choice, no compelling differentiator over NUnit or
  MSTest here.

## Reversibility

The framework is trivially swappable. The harness is the substantive part of this
decision, and it is additive - it removes nothing from `design.md` §10, which is adopted
whole.
