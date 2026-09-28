# Wire Format v1

Normative reference for the **byte form** of the PeerLink datagram of `design.md` §7.1 -
its fields, its canonicalization, its bounds, and the golden vectors that freeze them.

**Ingress is not specified here.** `design.md` §7.1 owns the validation pipeline and is
the only normative list for it. That division exists because revisions 3 and 4 had both
documents specifying ingress with different step counts, and the repository's own
precedence rule then deleted behaviour the pairing ceremony depends on.

**Version 1 is frozen.** `design.md` §8 draws the phase boundary *around* the wire format
for a specific reason: everything that touches the wire lands in phase 1 whether or not
its consumer exists yet, so that no change here ever becomes a two-machine migration.

## Why a canonical form is required

The `mac` is an HMAC-SHA256 over the payload, so sender and receiver must agree on the
exact bytes being signed, down to field order and number formatting. Two JSON documents
that are semantically identical but textually different produce different MACs.

This is the failure mode worth fearing, because it is silent. A canonicalization change
produces datagrams the peer rejects, the peer reports no peer, and §5.5 leaves both
machines audible. The symptom is byte-identical to "the other machine is switched off" -
correct behaviour, safe behaviour, and completely misleading.

It is also why a wire-version bump cannot be detected per datagram: the same property that
makes the `mac` meaningful makes an unverifiable datagram's `v` untrustworthy. §7.1's
rate-based producer is the compensating control.

## Payload

```json
{
  "v": 1,
  "pairId": "b1f0...",
  "machineId": "7f3a9c...",
  "seq": 41,
  "activeOwner": "2d81e4...",
  "micLive": false,
  "bye": false,
  "sentUtc": "2026-09-25T21:07:33.118Z",
  "mac": "..."
}
```

| Field | Type | Notes |
|---|---|---|
| `v` | integer | Format version. From an **authenticated** peer, an unknown value raises `error` (§7.1). An unverifiable one cannot be read at all |
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
2. `mac` is computed over the document with the `mac` field **omitted entirely** - not
   present-and-empty, not present-and-null.
3. UTF-8, no BOM.
4. No insignificant whitespace: no spaces after `:` or `,`, no newlines.
5. Hex strings are lowercase.
6. Integers are unsigned decimal with no leading zeros, no sign, no exponent.
7. Booleans are bare `true` / `false`.
8. `sentUtc` is `yyyy-MM-ddTHH:mm:ss.fffZ`, always UTC, always exactly three fractional
   digits.
9. No field is ever omitted. A missing field is a version mismatch, never a default -
   this is explicit in §7.1 for `micLive` and is applied to all fields here.

## Ingress

**`design.md` §7.1 owns ingress and is the only normative list.** It is not duplicated
here, because two documents specifying the same pipeline is how the §7.7 pairing exception
came to be missing from the authoritative one - see issue #2.

What this document owns, and §7.1 relies on:

- the canonical byte form the `mac` is computed over (above)
- the bounds below
- the golden vectors

One consequence of canonicalization is worth stating where the canonicalizer lives. Because
the `mac` covers the canonical form, **a receiver that cannot verify a datagram cannot
trust any field inside it, including `v`.** A peer on a later wire version produces a
different canonical form and therefore fails the `mac` check, so a version bump and a key
mismatch look identical per datagram. §7.1 handles that with a rate-based producer rather
than a per-datagram one; do not reintroduce a version check ahead of the `mac` here.

Drops are counted and logged **aggregated per minute by reason**, never one line per
datagram - see [ADR 0015](adr/0015-local-rolling-log.md). The rate of `pairId`-matching,
`mac`-failing drops is the signal §7.1's unverifiable-peer producer reads.

## Departure

`bye: true` is sent on graceful exit and on `WM_ENDSESSION` - never on the cancellable
query phase - three times, about 50 ms apart, because UDP offers no retry and no further
heartbeat is coming. A sender claiming on its way out broadcasts its state datagram first,
then the `bye`s.

On receipt, a `bye` that passes every ingress check does exactly three things:

1. **Bypasses §5.4.** Its `seq` and `activeOwner` are ignored, including when `seq` is
   strictly higher. A departure can never move the latch, which is what keeps §5.2 true.
2. **Clears peer presence immediately** rather than waiting out the 10 s window, and sets a
   departed flag. Presence is re-established only by the next accepted **non-`bye`**
   datagram. This rule has to be stated: a `bye` is itself a valid datagram, so a receiver
   that merely refreshed a last-seen timestamp would extend presence rather than clear it.
3. **Leaves the §7.6 quarantine observation latch alone.** A machine that observed its peer
   during a rejoin window still adopts that peer's state at expiry, however many `bye`s
   arrive afterwards.

Together these collapse the asymmetry `design.md` §9.2-5 calls "the wrong way round given
Goal 1" without handing a departure the power to move ownership or to steer a rejoining
machine.

**On replay and forgery.** Forgery is not a concern: the `mac` is keyed by `pairKey`, which
never crosses the network. Replay needs no key and §7.1 accepts it explicitly. With the
three rules above in place, a replayed `bye` can only clear presence, which unmutes - Goal
1's safe direction. No anti-replay rule is specified; one was considered and deliberately
dropped, because it would add mechanism to a frozen format to defend against a threat the
design already tolerates.

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
| `v1-bye-higher-seq` | `bye: true` with `seq` strictly above the receiver's, asserting `activeOwner` does **not** move |
| `v1-seq-zero` | `seq: 0`, guarding leading-zero and empty-integer formatting |
| `v1-seq-max` | `seq` at `uint64.Max`, the §5.4 bound case |
| `v1-owner-is-peer` | `activeOwner` naming the other roster entry |
| `v1-owner-unknown` | `activeOwner` outside the roster -> both audible, `error` |
| `v1-bad-mac` | One flipped bit in `mac` -> dropped at ingress step 2 |
| `v1-missing-miclive` | Field absent -> version mismatch, **never** inferred as `false` |
| `v1-missing-bye` | Field absent -> version mismatch, **never** inferred as `false` |
