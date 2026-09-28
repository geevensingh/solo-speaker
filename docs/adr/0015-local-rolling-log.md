# 0015 - A local rolling log, with aggregated ingress drops

**Status:** Accepted - 2026-09-27
**Resolves:** `implementation-plan.md` §8.5

## Context

`design.md` says nothing about diagnostics. Two of its recorded risks are specifically
*silent*, and neither is diagnosable from the tray alone:

- §9.2-2: an always-on mic consumer holds `selfMicLive` true forever, making a machine
  permanently unmutable. §7.4's discovery UI helps only once you already suspect it.
- `implementation-plan.md` §6.4: a wire-version mismatch presents as "no peer", which is
  byte-identical to the peer being switched off. Manual matrix row G7 asks you to tell
  those apart, and without a log there is nothing to tell them apart *with*.

## Decision

A rolling plain-text log at `%LOCALAPPDATA%\SoloSpeaker\logs\solospeaker-yyyyMMdd.log`.
UTF-8, one line per event, seven days retained, capped at 10 MB per day. No logging
framework - a small writer, to keep the single-file publish lean and avoid a
configuration surface nobody will use.

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
- **ingress drops, aggregated per minute by reason** - never one line per datagram

A tray menu item opens the log folder.

## Consequences

- The two silent failures above become diagnosable after the fact rather than only while
  reproducing them.
- The aggregation rule is the load-bearing detail. Two machines beating every two seconds
  produce 86,400 datagrams a day; logging drops individually would bury the signal in the
  noise it is made of. What matters is a *rate change* by reason - "4,300 bad-MAC drops
  this minute" is the sentence that identifies a version mismatch.
- The log contains roster IDs and `pairId`. It must never contain `pairKey`, and it is in
  `.gitignore` regardless.
- Distinguishing a clean `bye` from a timeout is what makes G7 answerable: a peer that
  said goodbye left deliberately; a peer that timed out either crashed, went out of range,
  or is speaking a wire version we reject.

## Reversibility

Trivial.
