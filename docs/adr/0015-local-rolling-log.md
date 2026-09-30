# 0015 - A local rolling log, with aggregated ingress drops

**Status:** Accepted - 2026-09-27
**Resolves:** `implementation-plan.md` §8.5

## Context

`design.md` says nothing about diagnostics. Two of its recorded risks are specifically
*silent*, and neither is diagnosable from the tray alone:

- §9.2-2: an always-on mic consumer holds `selfMicLive` true forever, making a machine
  permanently unmutable. §7.4's discovery UI helps only once you already suspect it.
- `implementation-plan.md` §6.4: a wire-version mismatch presents as "no peer", which is
  byte-identical to the peer being switched off. Manual matrix row G9 asks you to tell
  those apart, and without a log there is nothing to tell them apart *with*.

## Decision

A rolling plain-text log at `<data root>\logs\solospeaker-yyyyMMdd.log`. With the default
data root that is `%LOCALAPPDATA%\SoloSpeaker\logs\solospeaker-yyyyMMdd.log`. UTF-8, one
line per event, seven days retained, capped at 10 MB per day. At half the cap the current
file is rolled to a single `.1` sibling, overwriting any previous sibling, and a fresh
file starts. Current plus `.1` stays inside the cap while the most recent window is always
present. No logging framework - a small writer, to keep the single-file publish lean and
avoid a configuration surface nobody will use.

Logged at minimum:

- ownership changes, **with their source**: hotkey, tray click, external unmute, mic edge,
  peer adoption, quarantine adoption
- tray state transitions, with the producer that raised them
- presence gained and lost, distinguishing a clean `bye`
  ([0016](0016-goodbye-datagram-in-v1.md)) from a presence-window timeout
- ledger writes, replays, and clears
- default-endpoint changes
- hotkey registration result, success or failure
- pairing events, and pairing-mode entry and expiry
- quarantine entry and exit, with the reason for exit
- denylist hits
- **ingress drops, aggregated per minute by reason** - never one line per datagram. For
  each reason the aggregate carries the count and whether anything at all was accepted in
  the same window. The `pairId`-matching `mac`-failure rate corroborates `design.md`
  §7.1's unverifiable-peer producer rather than feeding it

A tray menu item opens the log folder.

**Amendment - work item 8, 2026-09-30.** The path above is relative to the resolved data
root, not a second hardcoded root. The literal `%LOCALAPPDATA%\SoloSpeaker\logs\...` path
remains true for the default root only. Work item 6 and its two-instance harness depend on
two instances with different roots legitimately running on one host; sending both logs to
one process-counted file would put diagnostics outside the only guard the product has.
The cap is a rolling tail, not a stop-writing switch: when the current file reaches half
the daily cap it moves to the single `.1` sibling, replacing that sibling, and a fresh file
starts. Keeping the first half of a flood and discarding everything after is the wrong
half. The `mac` aggregate also no longer drives tray behaviour. Since work item 3 the
reducer owns the unverifiable-peer producer; the log records the same count and whether
anything was accepted in that window so G9 has an independent observation rather than a
second implementation of the mechanism.

## Consequences

- The two silent failures above become diagnosable after the fact rather than only while
  reproducing them.
- The aggregation rule is the load-bearing detail. Two machines beating every two seconds
  produce 86,400 datagrams a day; logging drops individually would bury the signal in the
  noise it is made of. What matters is a *rate change* by reason, plus whether valid
  traffic was accepted in the same window - "4,300 `mac` failures on our own `pairId` this
  minute, nothing valid accepted" is the sentence that distinguishes an unverifiable peer
  from ordinary foreign noise. The producer itself lives in the reducer:
  `Reducer.Rejected` keeps `UnverifiableAt` timestamps inside the presence window and
  raises `ErrorCause.PeerUnverifiable` at `UnverifiableThreshold` when nothing valid has
  been accepted. The log reads the same condition; nothing reads the log.
- The log contains roster IDs and `pairId`. It must never contain `pairKey`, and it is in
  `.gitignore` regardless.
- Distinguishing a clean `bye` from a timeout is what makes G9 answerable: a peer that
  said goodbye left deliberately; a peer that timed out either crashed, went out of range,
  or is speaking a wire version we reject.

## Reversibility

Trivial.
