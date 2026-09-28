# 0011 - Pairing by bundle file, with in-band roster completion

**Status:** Accepted - 2026-09-27
**Resolves:** `implementation-plan.md` §8.1

## Context

`design.md` §7.7 describes the ceremony in three steps but leaves the transfer mechanism
open: machine A "displays them as a short code or QR", and then "the two exchange roster
IDs" with no mechanism at all for the second direction.

A short code cannot work. `pairId` (128 bits) plus `pairKey` (256 bits) plus a roster ID
(128 bits) is 512 bits - roughly 103 base32 characters. QR requires B to have a camera
pointed at A's screen, which is not a safe assumption for a desktop/laptop pair.

## Decision

A **bundle file** in one direction, and B's ordinary heartbeat in the other.

1. On A, `--pair-init` generates `pairId`, `pairKey`, and `rosterIdA`; writes `config.json`
   with a one-entry roster; enters **pairing mode** for a bounded window (default 10
   minutes); and writes `pairing.json` containing `pairId`, `pairKey`, and `rosterIdA`.
2. The user copies `pairing.json` to B by any means.
3. On B, `--pair-join <path>` generates `rosterIdB`, writes a complete two-entry
   `config.json`, **deletes the bundle**, and begins normal operation.
4. A receives B's ordinary heartbeat. It passes the `pairId` and HMAC checks but carries an
   unknown `machineId`. Because A's roster is incomplete *and* pairing mode is active, A
   enrolls `rosterIdB`, completes the roster, leaves pairing mode, and deletes its own copy
   of the bundle.
5. Both machines display an 8-hex-character fingerprint - the first four bytes of
   HMAC-SHA256 over the two roster IDs in sorted order, keyed by `pairKey`. The user
   compares them by eye.

## Consequences

- **No new wire message type.** Step 4 reuses the v1 datagram exactly; the only change is
  A's ingress behaviour while in pairing mode, where "drop if `machineId` is not in the
  roster" becomes "enroll it". This matters because §8 freezes the wire format in phase 1,
  and a pairing-specific message would have had to be designed and frozen alongside it.
- Enrollment still requires possession of `pairKey`, since the HMAC check runs first and
  unchanged. The bundle is the only way to obtain it.
- Pairing mode is time-bounded, so a machine is never indefinitely willing to enroll.
- `pairing.json` holds `pairKey` in cleartext, which is why both sides delete it and why
  it is in `.gitignore`. It is the one moment the key exists outside the two configs.
- The fingerprint is a human check, not a cryptographic one. It catches the realistic
  error - a stale bundle, or pairing the wrong pair of machines - rather than an attack.
- If A never sees B, pairing mode expires with an incomplete roster. Per §5.5 that machine
  can never mute, which is correct, and it raises `error` with cause "pairing incomplete"
  rather than looking healthy.

## Reversibility

Good. The ceremony touches nothing in the steady-state protocol, and re-pairing is already
the supported path for replacing a machine (§7.7).
