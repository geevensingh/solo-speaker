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
| `activeOwner` | hex string, 32 chars | A roster ID, or `MachineId.None` - 32 zeros - before the first claim (`design.md` §5). Never a hostname (§5.3) |
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

   **The receiver re-canonicalizes.** It parses the field values, re-emits rules 1 and 3
   through 9 from those values, and MACs *that* - never the bytes it received, and never
   the received bytes with `mac` textually excised. The two strategies **accept different
   byte sets**, so this is part of the v1 interop contract and not an implementation
   detail. It was unspecified through revision 5 of `design.md`; see issue #7.

   Two consequences follow, and both are load-bearing:

   - A datagram missing any signed field cannot be reconstructed and therefore cannot be
     verified. It dies at `design.md` §7.1 **step 2**, counted, rather than at step 5. That
     is what lets §7.1's unverifiable-peer producer fire at all, and it is why step 5 names
     unknown `v` alone.
   - A datagram whose *values* are correct but whose *bytes* are not canonical - extra
     whitespace, say - still verifies, because the MAC authenticates the values.
     Canonicalization exists so that sender and receiver agree on which bytes represent
     those values, not to make the received framing itself significant. Receivers
     nonetheless reject duplicate keys, unknown keys, trailing data, and non-canonical
     scalar forms, and attribute every such rejection to step 2.
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

- the canonical byte form the `mac` is computed over (above), **including which bytes the
  receiver MACs** - rule 2
- the bounds below
- the golden vectors

Note that rule 2 decides two things §7.1 states but does not derive: that a missing field
is a step 2 rejection rather than a step 5 one, and that a receiver which cannot verify a
datagram cannot trust any field inside it. A machine's own datagram is dropped at §7.1 step
3 before either question arises; that is ingress, and §7.1 owns it.

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

The vectors are **[`wire-format.vectors.json`](wire-format.vectors.json)**, which is
normative. They are not reproduced here: a byte-exact value hidden inside a fenced block in
a prose document is not reviewable - a trailing space or a stray `\r` is invisible in the
diff, which is precisely the silent failure this document opens by naming. A separate JSON
artifact keeps one source of truth while making each vector diff on its own line.

The test suite loads that file, fails if it is missing, and asserts the vector count and
names against the table below, so a vector cannot be quietly dropped. A suite that silently
parsed zero vectors would be green while asserting nothing, in the one place whose entire
job is to be hard to change by accident.

Each round-trip vector is a byte-exact canonical form with its expected MAC under a fixed
test `pairKey`. That key is a published constant, written in the vectors file and used
nowhere but the test assembly. It is not a secret and its publication is not the disclosure
`AGENTS.md` §6 forbids - it authenticates nothing, because the `pairKey` that rule protects
is the one generated by §7.7 and held only in `config.json` on the two real machines.

Honest bound on what this proves: the vectors were generated by the canonicalizer they
freeze, so they cannot catch an error that was present when they were written. What they
catch is *change* - any later edit to field order, number formatting, whitespace, or
endianness fails the test. The canonical forms are stored as readable UTF-8 strings rather
than hex for exactly this reason: a wrong field order is visible on review.

Changing a vector must therefore be a deliberate, reviewable act that forces the question
"does this need a `v` bump, and does it need both machines updated at once?" Per
[`implementation-plan.md`](implementation-plan.md) §6.4, the answer to the second half is
almost always yes.

### Round-trip vectors

Canonical bytes and MAC, asserted in both directions - encode produces these bytes, and
parsing these bytes yields these field values.

| Vector | Covers |
|---|---|
| `v1-baseline` | Nominal datagram, `micLive: false`, `bye: false`, mid-range `seq` |
| `v1-miclive-true` | Phase 2 shape, proving phase 1 and 2 are wire-compatible |
| `v1-bye` | `bye: true`, the departure datagram |
| `v1-bye-higher-seq` | `bye: true` with `seq` strictly above the receiver's, asserting `activeOwner` does **not** move. The behavioural half of that claim is the reducer's; this vector freezes the bytes it is asserted against |
| `v1-seq-zero` | `seq: 0`, guarding leading-zero and empty-integer formatting |
| `v1-seq-max` | `seq` at `uint64.Max`. Guards 64-bit parse precision - a double-based parser silently returns `18446744073709551616`. Also the §5.4 bound case; see the ingress table |
| `v1-owner-is-peer` | `activeOwner` naming the other roster entry |
| `v1-owner-unknown` | `activeOwner` outside the roster. Encodes and parses cleanly: "both audible, `error`" is a §5.5 **reducer** outcome, not an ingress rejection. Ingress step 3 tests `machineId`, never `activeOwner` |
| `v1-owner-none` | `activeOwner` is `MachineId.None`, the pre-claim state of `design.md` §5. Freezes the reserved value's encoding |
| `v1-date-padding` | Single-digit month and day, freezing rule 8's zero-padding |

### Ingress-outcome vectors

Input bytes plus the context they are evaluated against, and the expected first rejection
reason. `design.md` §7.1 owns the ordering; these assert the attribution.

| Vector | Covers | Outcome |
|---|---|---|
| `v1-foreign-pairid` | A well-formed datagram signed with another pairing's key | `ForeignPairId`, silent - step 1 |
| `v1-bad-mac` | One flipped bit in `mac` | `BadMac`, silent - step 2 |
| `v1-missing-miclive` | Field absent -> version mismatch, **never** inferred as `false`. Unverifiable under rule 2, so step 2 and not step 5 | `BadMac`, silent - step 2 |
| `v1-missing-bye` | Field absent -> version mismatch, **never** inferred as `false` | `BadMac`, silent - step 2 |
| `v1-unknown-key` | An extra field, the shape a v2 peer most likely takes. Must pass step 1 and die at step 2 so it feeds the unverifiable-peer producer; dying earlier would silently re-break issue #1 | `BadMac`, silent - step 2 |
| `v1-self-origin` | Our own heartbeat, heard back off the broadcast. Passes steps 1 and 2 by construction | `SelfOrigin`, silent - step 3 |
| `v1-not-in-roster` | Valid `mac`, `machineId` outside the roster, pairing window closed | `NotInRoster`, silent - step 3 |
| `v1-pairing-enrollment` | The same datagram with an incomplete roster and the pairing window open (`design.md` §7.7, ADR 0011) | `PairingEnrollment` - step 3's exception |
| `v1-seq-over-bound` | `seq` delta of 1001 above `localSeq` | `SeqOutOfBounds`, `error` - step 4 |
| `v1-seq-at-bound` | `seq` delta of exactly 1000 | `Accepted` - the boundary's other side |
| `v1-seq-below-local` | `seq` **below** `localSeq`, which §5.4 ignores silently. Guards the unsigned-subtraction wrap that would turn every reordered or replayed datagram into a sticky `error` | `Accepted`; §5.4 discards it |
| `v1-unknown-version` | `v: 2` with every field present and a valid `mac` - the only shape that reaches step 5 | `UnknownVersion`, `error` - step 5 |
| `v1-bye-bad-mac` | A `bye` with a corrupted `mac`. `bye` is exempt from §5.4, never from ingress | `BadMac`, silent - step 2 |
| `v1-oversize` | Over the 512-byte bound | `Unreadable`, silent - before step 1 |
| `v1-not-json` | Bytes that are not a JSON object | `Unreadable`, silent - before step 1 |
