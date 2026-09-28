# Wire Format v1

Normative reference for the PeerLink datagram of `design.md` §7.1. This document is the
source of truth for canonicalization; the golden vectors below are checked in as test
data, and the test that reads them is what keeps this file honest.

**Version 1 is frozen.** `design.md` §8 draws the phase boundary *around* the wire format
for a specific reason: everything that touches the wire lands in phase 1 whether or not
its consumer exists yet, so that no change here ever becomes a two-machine migration.

## Why a canonical form is required

The `mac` is an HMAC-SHA256 over the payload, so sender and receiver must agree on the
exact bytes being signed, down to field order and number formatting. Two JSON documents
that are semantically identical but textually different produce different MACs.

This is the failure mode worth fearing, because it is silent. A canonicalization change
produces datagrams the peer rejects, the peer reports no peer, and §5.5 leaves both
machines audible. The symptom is byte-identical to "the other machine is switched off" —
correct behaviour, safe behaviour, and completely misleading.

## Payload

```json
{
  "v": 1,
  "pairId": "b1f0…",
  "machineId": "7f3a9c…",
  "seq": 41,
  "activeOwner": "2d81e4…",
  "micLive": false,
  "bye": false,
  "sentUtc": "2026-09-25T21:07:33.118Z",
  "mac": "…"
}
```

| Field | Type | Notes |
|---|---|---|
| `v` | integer | Format version. An unknown value is a version mismatch → `error` |
| `pairId` | hex string, 32 chars | 128-bit pairing GUID. Transmitted; scopes the namespace |
| `machineId` | hex string, 32 chars | Sender's 128-bit roster ID |
| `seq` | integer | Lamport clock. Orders by *event count*, never wall-clock time |
| `activeOwner` | hex string, 32 chars | A roster ID. Never a hostname (§5.3) |
| `micLive` | boolean | **Always emitted explicitly**, including phase 1 where it is `false` |
| `bye` | boolean | Deliberate departure. Clears peer presence immediately; **never** alters `activeOwner`. See [ADR 0016](adr/0016-goodbye-datagram-in-v1.md) |
| `sentUtc` | ISO 8601 UTC, ms | Informational and logged only. **Never a drop condition** |
| `mac` | hex string, 64 chars | HMAC-SHA256 over the canonical form, keyed by `pairKey` |

`pairKey` is never transmitted. Revision 1 of the design keyed the MAC by a secret derived
from `pairId` while broadcasting `pairId` in the same datagram every two seconds, so one
captured packet was enough to forge every subsequent one.

## Canonicalization

1. Fields are serialized in exactly the order in the table above, `mac` last.
2. `mac` is computed over the document with the `mac` field **omitted entirely** — not
   present-and-empty, not present-and-null.
3. UTF-8, no BOM.
4. No insignificant whitespace: no spaces after `:` or `,`, no newlines.
5. Hex strings are lowercase.
6. Integers are unsigned decimal with no leading zeros, no sign, no exponent.
7. Booleans are bare `true` / `false`.
8. `sentUtc` is `yyyy-MM-ddTHH:mm:ss.fffZ`, always UTC, always exactly three fractional
   digits.
9. No field is ever omitted. A missing field is a version mismatch, never a default —
   this is explicit in §7.1 for `micLive` and is applied to all fields here.

## Ingress order

Per §7.1, in exactly this order. A datagram failing more than one check is attributed to
the **first** failure, which is what the ingress tests assert.

1. `pairId` mismatch → drop, silent
2. Invalid `mac` → drop, silent
3. `machineId` not in the roster → drop, silent
4. `seq` exceeds `localSeq` by more than 1000 → drop, raise `error`
5. Missing or unparseable `micLive` or `bye` → version mismatch, raise `error`

Only then is the datagram processed by §5.4's convergence rules.

Steps 1–3 are silent by design. They are the expected result of ordinary traffic from
another pairing or another application on the same port, and raising `error` for them
would make the tray icon meaningless. Step 4 is loud because it is either corruption or an
attack, and it is the case that could otherwise pin ownership permanently.

Drops are counted and logged **aggregated per minute by reason**, never one line per
datagram — see [ADR 0015](adr/0015-local-rolling-log.md). A rate change by reason is the
signal that distinguishes a wire-version mismatch from a switched-off peer.

## Pairing-mode exception to step 3

During the bounded pairing window ([ADR 0011](adr/0011-pairing-bundle-file.md)), a machine
whose roster holds one entry replaces step 3 with "enroll this `machineId`" instead of
"drop". Steps 1, 2, 4, and 5 are unchanged, so enrollment still requires a valid HMAC and
therefore possession of `pairKey`.

This is why pairing needs no message type of its own: roster completion rides on B's
ordinary first heartbeat. A dedicated pairing datagram would have had to be designed and
frozen into v1 alongside everything else.

## Departure

`bye: true` is sent on graceful exit, logoff, and shutdown — three times, about 50 ms
apart, because UDP offers no retry and no further heartbeat is coming.

A receiver that accepts a `bye` clears peer presence immediately rather than waiting out
the 10 s window, which collapses the asymmetry `design.md` §9.2-5 calls "the wrong way
round given Goal 1". It does **not** touch `activeOwner`: §5.2 is explicit that a peer
disappearing never moves ownership, and a `bye` is a peer disappearing with better
manners.

A forged `bye` causes an unmute, which is the safe direction and no worse than the replay
§7.1 already accepts.

## Bounds

| Bound | Value | Why |
|---|---|---|
| Max `seq` delta | 1000 | A single datagram with `seq = uint64.Max` would otherwise pin ownership forever and poison persisted state on **both** machines |
| Max datagram size | 512 bytes | Payload is ~200 bytes; anything larger is malformed. Keeps the parser off fragmented paths |
| Heartbeat cadence | 2 s | Plus an immediate extra send on any state change, so claims feel instant |
| Presence window | 10 s | Five missed beats |

## Golden vectors

> **Not yet populated.** These land with step 2 of
> [`implementation-plan.md`](implementation-plan.md) §7, alongside the canonicalizer.

Each vector is a byte-exact canonical form with its expected MAC under a fixed test
`pairKey`, stored as test data and asserted on every CI run. Any change to field order,
formatting, or whitespace fails the test.

That is the intent: changing a vector must be a deliberate, reviewable act that forces
the question "does this need a `v` bump, and does it need both machines updated at once?"
Per [`implementation-plan.md`](implementation-plan.md) §6.4, the answer to the second half
is almost always yes.

| Vector | Covers |
|---|---|
| `v1-baseline` | Nominal datagram, `micLive: false`, `bye: false`, mid-range `seq` |
| `v1-miclive-true` | Phase 2 shape, proving phase 1 and 2 are wire-compatible |
| `v1-bye` | `bye: true`, the departure datagram |
| `v1-seq-zero` | `seq: 0`, guarding leading-zero and empty-integer formatting |
| `v1-seq-max` | `seq` at `uint64.Max`, the §5.4 bound case |
| `v1-owner-is-peer` | `activeOwner` naming the other roster entry |
| `v1-owner-unknown` | `activeOwner` outside the roster → both audible, `error` |
| `v1-bad-mac` | One flipped bit in `mac` → dropped at ingress step 2 |
| `v1-missing-miclive` | Field absent → version mismatch, **never** inferred as `false` |
| `v1-missing-bye` | Field absent → version mismatch, **never** inferred as `false` |
