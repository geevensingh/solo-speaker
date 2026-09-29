# SoloSpeaker - Design Plan

**Status:** Revision 9 - settles the persisted shapes row 5 freezes; revision 8 corrected §7.1's replay claim and recorded the unbounded case as a risk; revision 7 closed the §7.6 and §7.4 gaps row 3 surfaced; revision 6 defined `activeOwner` before the first claim and dropped self-origin datagrams at ingress; revision 5 made the version-mismatch signal reachable and gave §7.1 sole ownership of ingress; revision 2 incorporated adversarial review findings DR-001...DR-006
**Author:** drafted with Copilot, 2026-09-25
**Target:** two Windows machines (one desktop, one laptop), single user

> **Revision 9 changes, 2026-09-29.** Six amendments from the three-critic review of the row 5
> persistence plan. §8 draws the phase boundary around persisted state, so these are the last
> shape decisions phase 1 gets to make cheaply.
>
> **The pairing ceremony now writes `state.json` as well as `config.json`.** §10 demands
> `error` when `state.json` is deleted and `config.json` survives, while §7.5 left an absent
> state file indistinguishable from an ordinary first run - both cannot hold, and nothing on
> disk told them apart. Writing state at pairing makes absence mean "someone deleted it", and
> it closes the reverse case too: a stale `state.json` surviving an uninstall would otherwise
> raise `error` on the first start **after a successful pairing ceremony**.
>
> **`seq` is bounded on disk as well as on the wire.** §5.4 bounds it in the datagram because
> one hostile value "poisons the persisted state on **both** machines". Nothing bounded the
> file, so a hand-edited or restored `state.json` walked straight past the check.
>
> **Configuration fields are now split by what they gate.** Identity-bearing fields are
> strict-reject; tunables fall back to a documented default and raise a cause that names the
> field. "Reject rather than coerce" is scoped to persisted state because coercion breaks
> §5.5's invariant - a denylist string is not that, and refusing the whole document over a
> typo in one takes the pair dark while telling the user to re-pair, which would not fix it.
>
> **Both files carry a `schema` integer**, where an unrecognised **higher** value is its own
> named outcome rather than a parse failure. The consumers are real: Goal 8's hand-editor
> needs to know which shape they are editing, and §12's installer upgrading over an existing
> install needs to know whether the config it found is the shape it understands.
>
> Also: the debounce field is named `debounceSeconds`, because under §8 the persisted key
> *is* the frozen artifact and a unit-bearing name is worth fixing before it freezes; and
> §7.4 gains a cause for a `pairKey` the configuration store refuses.

> **Revision 8 changes, 2026-09-28.** One correction, found by the three-critic review of the
> row 4 harness plan while deciding what "the blast radius stays bounded" should assert.
>
> **§7.1's replay claim was false in the direction that matters.** "The blast radius is your
> own speaker, and §5.5's positive predicate bounds it" does not hold for a replayed *state*
> datagram, because such a datagram carries `activeOwner = <the peer>` - the one value §5.5
> is looking for. It therefore satisfies the predicate rather than being excluded by it, and
> refreshes presence. If the peer has gone **without** a `bye`, a sustained replay keeps this
> machine muted for a peer that no longer exists: Goal 1 inverted, by the same shape as
> revision 6's self-origin defect and reached by a different route.
>
> It is recorded as §9.2-8 rather than fixed, because it cannot be fixed in band. A replay of
> the peer's most recent datagram is byte-identical to a live heartbeat, `sentUtc` is
> deliberately not a drop condition, and an anti-replay nonce would change a format §8 freezes
> in phase 1 - which `wire-format.md` already considered and dropped. D-1 is the compensating
> control, and a manual-matrix row covers what CI cannot reach.
>
> Revision 7 narrowed the neighbouring case - a replay after an accepted `bye` - and this
> revision states plainly what that rule does and does not cover, so the next reader does not
> have to re-derive the difference.

> **Revision 7 changes, 2026-09-28.** Six gaps found by the three-critic review of the row 3
> reducer plan. None is a new decision; each is a place this document said something it did
> not finish saying, and row 3 is the first code that cannot decline to answer.
>
> **§7.6 adopt-at-expiry had nothing to adopt from.** "The peer's `(activeOwner, seq)` is
> adopted" required a value the window never recorded, and it cannot be recovered by applying
> §5.4 as observations arrive - §10's own row is "adopt peer's owner **even when our `seq` is
> higher**", which is exactly the case §5.4 says to ignore. The window now records the
> observed pair, last-writer-wins.
>
> **§7.6 never said what a quarantined machine broadcasts.** It says the machine does not
> broadcast its persisted ownership, while `wire-format.md` rule 9 forbids omitting a field -
> so a datagram sent during the window had to carry something, and two of the three available
> answers reintroduce the lid-open defect this section exists to prevent. Broadcasting
> `activeOwner = None` at a persisted `seq` lets the running peer adopt it under §5.4, so
> **opening a lid would erase ownership**. A quarantined machine now sends nothing.
>
> **§7.6 never said what a restart does to the observation latch.** A restart begins a new
> window, so "nothing clears it for the remainder of the window" did not answer the question.
> The latch now survives, because the alternative - a Wi-Fi blip during a legitimate rejoin
> erasing a correct observation - reaches the lid-open defect through an ordinary network
> transition.
>
> **§7.1's replay tolerance did not bound the case it claimed to.** "The blast radius is your
> own speaker, and §5.5's positive predicate bounds it" is false for a replayed state
> datagram: it carries `activeOwner = peer`, the one value that satisfies §5.5. A replay
> therefore holds presence indefinitely and undoes a `bye`, leaving a machine **muted for a
> peer that has gone**. Presence after a departure now requires new information, not an echo.
> No attacker is needed - this section already concedes that a machine bridging two
> interfaces onto one subnet echoes broadcasts back.
>
> **§7.1's "sustained" was never quantified**, and the reducer needs a number. It is three
> unverifiable datagrams in a sliding ten-second window. A real version-mismatched peer beats
> at exactly five per ten seconds, so a threshold of five would sit on the rate it must
> detect.
>
> **§7.4's table was left non-total by revision 6.** With `activeOwner == None` and the peer
> present, all five states were false by their own definitions. `unclaimed` is added, and
> [ADR 0014](adr/0014-tray-icons-by-shape.md) grows a sixth silhouette. In phase 1 this is not
> a transient: the only writer is a manual claim, so a freshly paired pair sits here until
> somebody presses the hotkey.

> **Revision 6 changes, 2026-09-28.** Two Critical gaps found by the three-critic review of
> the phase 1 rows 1-2 implementation plan. Both had to be settled here rather than in code,
> because both fix the domain of a field §8 freezes in phase 1.
>
> **`activeOwner` had no value before the first claim.** §5 typed it non-optional, §5.1 was
> its only writer, and `wire-format.md` rule 9 forbids omitting a field - so the heartbeat
> between "pairing completes" and "someone claims" had to carry a 128-bit value no document
> named. §5 now reserves `MachineId.None`. The alternative - each machine writing `self` on a
> fresh pair - makes §5.4's tiebreak fire at `seq = 0` and mutes one machine seconds after
> pairing with nobody in a call, which is Goal 5 violated by a default nobody chose.
>
> **A machine's own datagram passed every ingress check.** Its `pairId` is ours, our own
> `pairKey` signs it, and §5.3's roster contains *self* - so §7.1 step 3's "not in the roster"
> test passed, and §7.1's presence rule says "a valid datagram", never "from the peer". A
> machine therefore kept itself present forever: claim on the laptop, desktop mutes, laptop
> loses power without a `bye`, and the desktop's own heartbeats hold `peerPresent` true while
> §5.5 stays satisfied. **Muted, indefinitely, for a peer that no longer exists** - which
> falsifies §5.5's own "No peer -> never muted" and is a Goal 1 violation reached by the most
> ordinary event the product has. Step 3 now drops self-origin datagrams first.
>
> Also: §7.1 step 5 contradicted §7.1's own prose and §7.4's producer table, which is issue
> #2's defect class inside the one list revision 5 made normative. Step 5 is now "unknown `v`
> from an authenticated peer" alone. The missing-field arm is unreachable by construction once
> `wire-format.md` rule 2 fixes the receiver's MAC strategy, so it is struck rather than left
> standing as a producer nothing can raise - the defect §7.4 exists to prevent.

> **Revision 5 changes, 2026-09-28.** Closes issues #1 and #2 from the 2026-09-28 review.
>
> §7.1's ingress list is now **the only normative one**; `wire-format.md` keeps
> canonicalization, bounds, and the golden vectors and defers here. Revisions 3 and 4 left
> two documents specifying ingress with different step counts, and the stated precedence
> rule silently deleted the §7.7 pairing exception - so an implementer building from the
> authoritative document would have shipped a product that could not be paired.
>
> The version-mismatch `error` producer is rebuilt, because the one revisions 3 and 4
> specified could never fire. The `mac` covers the canonical form, so an unverifiable
> datagram's `v` cannot be trusted, and a later-version peer fails at the `mac` check
> before any version check is reached. A version bump and a key mismatch are
> indistinguishable per datagram; that is inherent. What is detectable is the *rate* of
> `pairId`-matching, `mac`-failing traffic while nothing valid is being accepted - see
> §7.1. The second clause keeps a public `pairId` from handing a stranger a
> tray-disabling denial of service.

> **Revision 4 changes, 2026-09-28.** The adversarial review recorded in
> [`review-2026-09-28.md`](review-2026-09-28.md) found two Critical defects in revision 3's
> `bye` amendment. Both are fixed here.
>
> §5.4 now states that a `bye` bypasses convergence entirely, so a departure cannot move
> the latch - revision 3 specified the sender's discipline and never the receiver's
> enforcement. §7.6's peer observation becomes a latch that a `bye` cannot clear, because
> presence is quarantine's expire-versus-adopt input and revision 3 gave `bye` the power to
> clear presence without noticing what else read it. §7.1 now names the presence
> re-acquisition rule, which no revision had specified. An anti-replay rule for `bye` was
> considered and deliberately dropped: after these fixes a replayed `bye` can only unmute,
> which is within the tolerance §7.1 already grants replay.
>
> §7.6's uninstall row is also corrected: the uninstaller aborts without deleting anything
> when `--restore` fails, rather than removing the binary and the ledger behind it.

> **Revision 3 changes, 2026-09-27.** One material change: a `bye` boolean joins the §7.1
> payload and §7.6's unmute paths, so a machine leaving deliberately tells its peer instead
> of being timed out. It narrows §9.2-5, which revision 2 recorded as bounded-not-fixed.
> This lands now rather than when convenient because §8 freezes the wire format in phase 1,
> which makes a later field a coordinated two-machine update - see
> [`adr/0016-goodbye-datagram-in-v1.md`](adr/0016-goodbye-datagram-in-v1.md).
>
> Also in revision 3, and changing no decision: the product is renamed `SoloSpeaker`
> throughout, including the `%LOCALAPPDATA%` paths
> ([ADR 0007](adr/0007-solospeaker-naming.md)); §7.7's unspecified pairing transfer is
> settled ([ADR 0011](adr/0011-pairing-bundle-file.md)); and four gaps this document never
> covered are settled in ADRs 0012-0015 - a single-instance guard, `pairKey` protection at
> rest, tray icon assets, and diagnostics. How the design gets built, tested, and deployed
> is [`implementation-plan.md`](implementation-plan.md).

> **Revision 2 changes.** An adversarial review of revision 1 found four Critical and
> several High defects. The material changes: the mute predicate now consults microphone
> state and is stated in positive space against an enrolled roster (§5.5); `pairKey` is now
> a separate never-transmitted secret (§7.1); mic detection is split into a fast safety
> signal and a slow ownership signal (§7.2); a mutation ledger with crash recovery is added
> (§7.3, §7.6); and §8's phases are re-cut so that every wire-format, pairing, and
> persisted-schema decision lands in phase 1. Three decisions previously left open are now
> recorded in §9 and marked for confirmation.

---

## 1. Problem

The user runs a desktop and a laptop. Meetings are joined on one or the other, and which
one changes throughout the day. When both machines are physically together, exactly one
should produce audio; the other should be globally muted so notification sounds, ringtones,
and duplicated meeting audio don't overlap.

When the machines are apart - laptop off, laptop elsewhere, or user working away from the
desk - both machines should be unmuted, because there is no conflict to resolve.

## 2. Goals

Stated as a priority ordering, not a set of co-equal invariants. Where they conflict, the
earlier goal wins - this ordering is load-bearing and is referenced by §5.5 and §9.

1. **Fail audible.** No reachable state leaves a machine muted with nothing running to
   unmute it. Silence is never the safe default. This outranks everything below.
2. **Never mute a machine that is capturing audio.** A machine in a call stays audible even
   if that means both are briefly audible.
3. **In steady state, exactly one machine unmuted while in proximity.** Steady state means
   "absent an in-flight transition" - transient both-unmuted windows are permitted and
   bounded in §9.
4. **Both unmuted whenever not in proximity.**
5. **Ownership changes only on deliberate events** - a call starting, or a manual claim.
   Never on idle time, focus, lock state, or a machine merely appearing.
6. No cloud service, no account, no broker. Local network only.
7. Same binary on both machines; no privileged role in normal operation.
8. Recoverable by hand at any moment, including after the app is gone.

## 3. Non-goals

- Coordinating more than two machines. As of revision 2 the design *does* actively prevent
  it: the roster (§5.3) is fixed at two entries and the convergence tiebreak assumes
  exactly two. Supporting N>2 would be a redesign, not an extension.
- Muting or managing the **microphone**. Only the render (output) endpoint is controlled.
- Per-application muting. This is a global endpoint mute.
- Cross-network / remote operation. Proximity is the whole point.
- Any integration with Teams/Zoom/Slack APIs. Detection is OS-level and app-agnostic.

## 4. Rejected approach: most-recent-input arbitration

An earlier draft chose the active machine by comparing idle time (`GetLastInputInfo`)
across the pair, lowest idle wins.

This was rejected outright. It is wrong for the actual use case: during a meeting the user
may not touch the meeting machine at all for long stretches while typing on the other one,
which would hand "active" to the wrong machine mid-call. It also flaps - every glance at
the other keyboard moves the mute - requiring hysteresis to paper over a model that was
wrong to begin with.

Recorded here so it does not get re-proposed.

## 5. Core model

A single replicated value:

```
(activeOwner: MachineId, seq: uint64)
```

`activeOwner` is whichever machine is allowed to make sound. `seq` is a monotonic counter
used to order updates.

**Before the first claim, `activeOwner` is the reserved value `MachineId.None`** - 128 bits
of zero, which enrollment never generates and which therefore names no machine. A freshly
paired machine holds it until §5.1's writers fire for the first time, and broadcasts it
explicitly like any other value, because `wire-format.md` rule 9 forbids omitting a field.

§5.5's predicate is false on both machines while it holds, so both stay audible until
somebody deliberately claims - which is Goal 1's direction and leaves §5.1 as the only thing
that ever creates ownership. The alternative considered was each machine writing `self` on a
fresh pair; that makes both sides disagree at `seq = 0`, fires §5.4's tiebreak, and mutes the
machine with the lexicographically larger roster ID seconds after pairing, with nobody in a
call. Ownership moving without a deliberate event is exactly what §5.2 exists to forbid.

### 5.1 The only two writers

State changes on exactly two events, both **edge-triggered**, both local to a machine:

| Event | Trigger |
|---|---|
| **Call started** | Rising edge of microphone-in-use on this machine |
| **Manual claim** | Global hotkey pressed, or tray icon clicked, on this machine |

Either event performs:

```
seq       = max(localSeq, lastSeenPeerSeq) + 1
activeOwner = self
persist(activeOwner, seq)
broadcast()
```

### 5.2 What is explicitly NOT a writer

These deliberately do nothing:

- **A call ending.** Active does not revert. If the last manual claim was the desktop and
  the user takes a call on the laptop, the laptop stays active after that call ends.
- **A call starting on the machine that is already active.** No-op.
- **Idle time, input, focus, lock state, foreground window.** Never consulted.
- **Peer appearing or disappearing.** Changes mute output (§5.5) but never `activeOwner`.

This stickiness is the central property of the design. The active machine is a latch, not
a function of current conditions.

### 5.3 The roster

`activeOwner` is not a free-form hostname. At pairing time (§7.1) exactly two opaque
128-bit machine IDs are enrolled and written to both machines' config. That pair is the
**roster**, and together with the reserved `MachineId.None` of §5 it is the complete domain
of `activeOwner`.

`MachineId.None` is 128 bits of zero. It is never generated at enrollment, is never a valid
roster entry, and any path that reads a roster ID must reject it - a roster containing
`None` would satisfy §5.5's `activeOwner == peerRosterId` and mute a machine for a peer that
does not exist. It is a value `activeOwner` may hold, not a machine that may be enrolled.

This exists because revision 1 let `activeOwner` hold any string. Renaming a PC, replacing
a machine, or retiring an old device that once held the `pairId` could leave `activeOwner`
naming nobody - at which point both machines satisfy "I am not the owner" and both mute,
persistently, across reboots, with no failsafe covering it.

IDs are generated at enrollment, never derived from hostname, and never change when Windows
is renamed. Comparison is ordinal byte equality - no casing, culture, or normalization
semantics anywhere in the comparison path.

### 5.4 Convergence

Each machine broadcasts its current `(activeOwner, seq)`. On receipt, after the datagram
passes the §7.1 authentication and roster checks:

- `peerSeq > localSeq` -> adopt peer's `(activeOwner, seq)`.
- `peerSeq < localSeq` -> ignore; our next broadcast will correct the peer.
- `peerSeq == localSeq` and owners agree -> no-op.
- `peerSeq == localSeq` and owners **disagree** -> concurrent edges. Break the tie by the
  lexicographically smaller roster ID, evaluated identically on both sides, then bump `seq`
  so the resolution propagates.

**A datagram carrying `bye: true` never reaches these rules.** It is consumed by the
presence layer alone (§7.1), and its `seq` and `activeOwner` are ignored in both directions
of ordering. The fields are still present and still signed, because canonicalization
forbids omitting them, but they are not read.

This is not an optimisation. Without it, a departing machine holding the latest `seq`
broadcasts `bye: true` with `activeOwner = self`, the receiver runs the rules above, sees
`peerSeq > localSeq`, and adopts - so a **departure** moves the latch. That contradicts
§5.2, which is the central property of the design, and leaves the receiver holding
`activeOwner = <absent peer>`, primed to mute the instant that peer returns. Revision 3
specified the sender's discipline and not the receiver's enforcement, which is the same
mistake in a different place.

`seq` is `max(localSeq, lastSeenPeerSeq) + 1` - a Lamport clock. Note that this orders by
*event count*, not wall-clock time, which is the cause of the resume-from-sleep problem
addressed in §7.6.

Accepted `seq` values are bounded: a datagram whose `seq` exceeds `localSeq` by more than
1000 is dropped and raises the `error` tray state. Without this, a single malformed or
hostile datagram carrying `seq = uint64.Max` permanently pins ownership and poisons the
persisted state on both machines.

### 5.5 Mute decision

Derived, not stored. Evaluated every tick. Stated in **positive space** - a machine mutes
only when it can affirmatively name the peer as owner, never merely because it failed to
recognise itself:

```
shouldMute =   peerPresent
            && activeOwner == peerRosterId     // positive, not (activeOwner != self)
            && !selfMicLive                    // safety override, see §7.2
```

Three consequences, each deliberate:

- **`activeOwner == peerRosterId`, not `activeOwner != self`.** If `activeOwner` is
  corrupt, unknown, or names a retired machine, the predicate is false on *both* machines
  and both stay audible. Goal 1 holds by construction rather than by failsafe. The
  mismatch also raises the `error` tray state so the condition is visible rather than
  silently benign.
- **`!selfMicLive`.** A machine that is capturing audio is never muted, whatever the latch
  says. This is the fix for the defect where a spurious mic edge on the idle machine -
  a pre-join screen, a notification chime, an un-denylisted always-on consumer - took
  ownership and then permanently silenced the machine actually in the meeting.
- **No peer -> never muted**, regardless of `activeOwner`. Stored ownership survives the
  peer's absence and applies again on return.

The safety override does not touch §5.1's writers, so the stickiness requirement is
unaffected: in the scenario "last claim on desktop, call taken on laptop, call ends," the
laptop remains owner and the desktop remains muted, exactly as before. The override only
ever *relaxes* muting; it can never cause a mute that would not otherwise occur.

## 6. Components

```
┌─────────────────────────────────────────────┐
│  Tray app (one process per machine)         │
│                                             │
│  ┌──────────────┐   ┌────────────────────┐  │
│  │ MicWatcher   │   │ HotkeyListener     │  │
│  │ (rising edge)│   │ + TrayIcon         │  │
│  └──────┬───────┘   └─────────┬──────────┘  │
│         │  events             │             │
│         ▼                     ▼             │
│      ┌───────────────────────────┐          │
│      │  StateMachine  (pure)     │          │
│      │  activeOwner, seq         │          │
│      └──────┬─────────────┬──────┘          │
│             │             │                 │
│             ▼             ▼                 │
│      ┌────────────┐  ┌──────────────┐       │
│      │ PeerLink   │  │ MuteActuator │       │
│      │ (UDP)      │  │ (Core Audio) │       │
│      └────────────┘  └──────┬───────┘       │
│             │               │               │
│      ┌──────▼───────┐  ┌────▼─────────┐     │
│      │ StateStore   │  │ Ledger       │     │
│      │ (disk)       │  │ (disk)       │     │
│      └──────────────┘  └──────────────┘     │
└─────────────────────────────────────────────┘
```

`StateMachine` is a pure reducer: `(currentState, event, now) -> (newState, effects)`.
All I/O lives at the edges. This is the main testability decision - the entire arbitration
model becomes unit-testable without sockets, audio devices, or a second machine.

## 7. Detailed design

### 7.1 PeerLink - discovery and heartbeat

- **Transport:** UDP, IPv4 broadcast to the subnet broadcast address, fixed port (default
  `48292`, configurable).
- **Cadence:** every 2s, plus an immediate extra send on any state change so claims feel
  instant rather than up-to-2s-late.
- **Presence:** peer considered present if a datagram **from the peer** passed every check
  in the ingress list below within the last 10s (5 missed beats). Naming the sender matters:
  a machine hears its own broadcasts, and a presence rule phrased as "a valid datagram"
  makes every machine permanently its own peer - see the self-origin paragraph below. Loss
  of presence unmutes; it does not alter `activeOwner`.
- **Pairing:** a one-time ceremony (§7.7) produces three artifacts written to both
  machines: a `pairId` GUID that scopes the broadcast namespace, a **`pairKey`** 256-bit
  secret used for authentication, and the two-entry **roster** of §5.3. `pairId` is
  transmitted; `pairKey` and the roster are never transmitted.

Payload (JSON, ~200 bytes):

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

- **Authentication:** `mac` is an HMAC-SHA256 over the canonicalized payload, keyed by
  **`pairKey`**. Revision 1 keyed it by a secret "derived from `pairId`" while broadcasting
  `pairId` in the same datagram every 2 seconds - meaning anyone who received one packet
  could forge every subsequent one, and the authentication delivered none of the property
  it claimed. `pairKey` is generated at pairing and exists only on disk on the two
  machines.
- **Ingress order.** This list is normative and is the only one. `wire-format.md` owns
  canonicalization, bounds, and the golden vectors, and defers to this section for ingress.

  1. `pairId` mismatch -> drop, silent.
  2. Invalid `mac` -> drop, silent per datagram. See the unverifiable-peer producer below.
  3. `machineId` is **our own** -> drop, silent. Evaluated before anything else in this
     step, so a machine can never enroll itself. Then: `machineId` not in the roster ->
     drop, silent. **Excepted during the §7.7 pairing window**, where a machine whose roster
     holds one entry enrolls the sender instead. Steps 1, 2, 4 and 5 are unchanged by that
     exception, so enrollment still requires a valid `mac` and therefore possession of
     `pairKey`.
  4. `seq` delta exceeds the §5.4 bound -> drop, raise `error`.
  5. Unknown `v` from an authenticated peer -> drop, raise `error`.

  Then: `bye: true` goes to the presence layer only; `bye: false` goes to §5.4.

  Steps 1-3 are silent because they are the expected result of ordinary foreign traffic on
  the port - and, for the self-origin case, of ordinary *local* traffic - and an error icon
  that is always lit explains nothing. Steps 4 and 5 are loud because they can only be
  corruption, an attack, or a peer this machine cannot work with.

- **Self-origin.** *(revision 6)* An IPv4 subnet broadcast is delivered to every local
  socket bound to the port, including the sender's own, so a machine hears its own
  heartbeat. Every check above passes for it: the `pairId` is ours, our own `pairKey` signs
  it, and §5.3's roster contains *self*. Without step 3's first clause the machine counts
  itself as its own peer, and because presence below is defined on "a valid datagram" the
  effect is permanent: a machine whose peer has vanished without a `bye` keeps
  `peerPresent` true from its own beats, leaves §5.5 satisfied, and **stays muted for a
  peer that no longer exists**. The drop is silent and unconditional, and it precedes the
  §7.7 exception so that a machine mid-pairing cannot enroll itself as its own peer.

  This does not depend on loopback delivery being enabled. Replay is accepted outright
  below, and a machine bridging two interfaces onto one subnet - the topology §9.2-1
  contemplates - echoes its own broadcast back with no attacker involved.

- **A missing field is not a step 5 case.** `wire-format.md` rule 2 makes the receiver
  re-canonicalize the field values it parsed, so a datagram missing any signed field cannot
  be reconstructed and therefore cannot be verified: it dies at step 2, counted, feeding the
  unverifiable-peer producer below. Revisions 3 to 5 listed "a missing or unparseable field"
  under step 5, which contradicted this section's own `micLive` paragraph and §7.4's
  "from an **authenticated** peer". Step 5 is reachable only for a datagram whose `mac`
  verifies and whose `v` this machine does not know.

- **The unverifiable peer.** *(revision 5)* Step 5 cannot catch a wire-version mismatch,
  and no ordering of these checks can. The `mac` covers the canonical form, so a receiver
  that cannot verify a datagram cannot trust any field inside it - including `v` - and a
  peer running a later version produces a different canonical form and therefore fails at
  step 2. **A version bump and a key mismatch are indistinguishable per datagram.** That is
  inherent, and revisions 3 and 4 both specified a step-5 producer that could never fire.

  The signal that does exist is a rate. `pairId` is checked first and is transmitted in
  cleartext, so a foreign datagram dies at step 1 while a version-mismatched peer dies at
  step 2 *having passed step 1*. Sustained `pairId`-matching, `mac`-failing traffic is
  therefore "something claims to be my pairing and I cannot verify any of it".

  `error` is raised, with cause **"peer unverifiable - version or key mismatch"**, when
  that traffic is sustained **and** no valid datagram has been accepted within the presence
  window. The second clause is load-bearing: `pairId` is public, so without it any stranger
  could pin the tray into a sticky `error` and destroy the only visible explanation for why
  a machine is silent. If the real peer is still being heard, nothing is wrong.

  *(revision 7)* **Sustained** is three such datagrams within a sliding ten-second window -
  the presence window, so the two clauses are measured over the same span. The threshold is
  set below the peer's own beat rate rather than at it: a real version-mismatched peer
  broadcasts every 2 s, which is exactly five per ten seconds, so a threshold of five would
  sit on the rate it exists to detect and a single dropped packet would silence the signal.
  Three leaves margin in both directions - comfortably above stray foreign traffic that
  happens to share our `pairId`, comfortably below a genuine mismatched peer. The window
  slides rather than tumbles; a tumbling counter misses four-then-four across a boundary.

- **`micLive`** is always emitted explicitly, including in phase 1 where it is hardcoded
  `false`. An absent field must never be inferred as `false`. In practice a datagram
  missing it fails at step 2 rather than step 5, because the sender that omitted it signed
  something this receiver cannot reconstruct - which is exactly the unverifiable-peer case
  above.
- **`bye`** *(added revision 3, semantics corrected revision 4 - see
  [ADR 0016](adr/0016-goodbye-datagram-in-v1.md))* marks a deliberate departure. It is sent
  on graceful exit and on `WM_ENDSESSION` - never on the query phase, which the user can
  still cancel - three times about 50 ms apart, since UDP offers no retry and no further
  heartbeat follows. A sender that is claiming on its way out broadcasts its state datagram
  **first**, per the immediate-extra-send rule above, and only then the `bye`s.

  On receipt, a `bye` that passes every ingress check:

  1. **bypasses §5.4 entirely.** Its `seq` and `activeOwner` are ignored. It can never move
     the latch, which is what keeps §5.2 true.
  2. **clears peer presence immediately** rather than waiting out the 10s window, and sets
     a departed flag. Presence is re-established only by the next accepted **non-`bye`**
     datagram **carrying information we have not already seen** - a `(seq, activeOwner)`
     pair differing from the last one accepted. Naming the re-acquisition rule matters: a
     `bye` is itself a valid datagram, so a receiver that only refreshed a last-seen
     timestamp would *extend* presence rather than clear it.

     *(revision 7)* The "new information" clause is the second half of that rule, and it
     exists because the replay tolerance below does **not** bound this case. A replayed
     state datagram carries `activeOwner = peer`, which is precisely the value §5.5's
     predicate is looking for, so an echo refreshes presence *and* undoes the departure -
     leaving a machine muted for a peer that has gone. A departure is undone by news, not
     by an echo. No attacker is required: the self-origin paragraph above already concedes
     that a machine bridging two interfaces onto one subnet echoes broadcasts back, and the
     same bridge echoes the *peer's* last datagram after the peer powers off.
  3. **does not clear the §7.6 quarantine observation latch.** See §7.6.

  Like `micLive`, it is always emitted explicitly and an absent field is a version
  mismatch. A replayed `bye` clears presence, which unmutes - Goal 1's safe direction, and
  within the replay tolerance §7.1 already grants. Forgery is not a concern: the `mac` is
  keyed by `pairKey`, which never crosses the network.
- **Clock skew:** `sentUtc` is informational and logged, but is **not** a drop condition.
  Revision 1 rejected datagrams more than 60s from local time, which bought nothing
  (ordering relies on `seq`) and could sever the pair entirely on clock drift.
- **Replay** within the presence window remains possible and is accepted. *(revision 8)*
  Earlier revisions claimed "the blast radius is your own speaker, and §5.5's positive
  predicate bounds it." **The second half of that is false**, and it is worth stating
  precisely what is and is not bounded, because the difference is a Goal 1 case.

  What a replayed state datagram provably cannot do: move `activeOwner`, advance `seq`,
  cause a persisted write, raise an `error`, or re-establish presence after a `bye` - the
  last by the "new information" rule above.

  What it can do: **hold presence**. A replayed datagram carries `activeOwner = <the peer>`,
  which is precisely the value §5.5's predicate is looking for, so it satisfies the predicate
  rather than being excluded by it. If the peer has gone **without** a `bye` - a hard kill, a
  power loss, walking out of range - a sustained replay keeps this machine muted for a peer
  that no longer exists.

  This is accepted rather than fixed, and the reasoning is that it cannot be fixed in band. A
  replay of the peer's *most recent* datagram is byte-identical to a live heartbeat, and
  `sentUtc` is deliberately not a drop condition (above), so no receiver-side rule can
  separate them. An anti-replay nonce would be a change to a format §8 freezes in phase 1,
  and `wire-format.md` already records that anti-replay was considered and dropped.

  The compensating control is §9.1's decision D-1: an external unmute of an endpoint we muted
  is treated as a manual claim, so reaching for the volume flyout - the natural reflex when a
  machine is unexpectedly silent - takes ownership and ends the mute. See §9.2-8.

### 7.2 MicWatcher - two signals, not one

Primary mechanism: WASAPI. Enumerate the default **capture** endpoint's sessions via
`IAudioSessionManager2::GetSessionEnumerator`, and treat "at least one session in
`AudioSessionStateActive`" as raw mic activity. Poll at 1s.

Revision 1 derived a single debounced signal and used it only to fire ownership claims.
That was wrong in both directions: the debounce guaranteed an audio outage at the start of
every call, and was simultaneously too short to outlast a pre-join screen. The signal is
now split, because the two consumers want opposite biases.

| | `selfMicLive` (safety, §5.5) | claim edge (ownership, §5.1) |
|---|---|---|
| **Bias** | Broad and fast - prefer a false positive | Narrow and slow - prefer a false negative |
| **Denylist applied** | Yes | Yes |
| **Debounce applied** | **No** - engages the instant the mic opens | **Yes** - default 5s |
| **Consequence of being wrong** | Both machines audible, briefly | Nothing; the latch does not move |

The safety signal engaging immediately is what removes the start-of-call outage: the
machine becomes audible at mic-open, and the ownership write follows 5s later once the
session has proven durable. A pre-join screen the user abandons therefore makes that
machine audible for as long as they sit on it - but never moves ownership.

Known hazards:

| Hazard | Effect | Mitigation |
|---|---|---|
| Teams/Zoom open the mic on the **pre-join screen** | Would claim before the user joins | Debounce gates the ownership write only; safety signal is unaffected |
| Always-on mic consumers (NVIDIA Broadcast, Krisp, voice assistants, headset utilities) | Mic never goes idle; no rising edge ever fires again, **and** the machine becomes permanently unmutable via the safety signal | Denylist of process names. This is the failure mode with the worst blast radius, so the denylist needs a discovery affordance - see §7.4 |
| Mic opened for a notification chime | Spurious claim | Debounce + denylist |
| Machine is muted and then joins a call | Must still be able to claim | Render-endpoint mute does not affect capture sessions, so the edge still fires. **Assumption, not verified** - see §9 |
| Both machines mic-live simultaneously | Neither can be muted by §5.5 | Accepted: both audible. Ownership still resolves normally underneath |

The denylist has the wrong polarity for an open-ended domain - it must enumerate every
bad actor, and its failure mode is silent. Mitigated in §7.4 rather than here.

A secondary detection path exists - the registry
`HKCU\...\CapabilityAccessManager\ConsentStore\microphone\...` keys, where `LastUsedTimeStop == 0`
means live. Not needed for arbitration, but a useful cross-check during development and a
fallback if WASAPI enumeration proves unreliable for some app.

### 7.3 MuteActuator and the mutation ledger

- `IMMDeviceEnumerator::GetDefaultAudioEndpoint(eRender, eMultimedia)` ->
  `IAudioEndpointVolume::SetMute`.
- Idempotent by construction. **Not** `VK_VOLUME_MUTE` - that is a toggle and will drift
  permanently out of sync the first time anything else touches it.
- Reconcile every tick: if actual mute state != desired, correct it.
- Subscribe to `IMMNotificationClient` for default-device changes so switching headset <->
  speakers re-applies the desired state to the new endpoint, and so the *previous* endpoint
  is released (see ledger below).

**The mutation ledger.** This app mutates sticky, OS-global, reboot-surviving state.
Revision 1 recorded nothing about those mutations, which made an orphaned muted endpoint a
certainty of ordinary use rather than an edge case, and left no way to recover one after
the app was gone. Every endpoint this app has muted is therefore recorded, before the
mutation, to `%LOCALAPPDATA%\SoloSpeaker\ledger.json`:

```json
{ "endpointId": "{0.0.0.00000000}.{9c8...}", "priorMute": false, "mutedAtUtc": "..." }
```

- Written **before** `SetMute`, flushed to disk, then the mutation is applied. An entry may
  therefore describe a mute that never happened; that is the safe direction.
- Cleared per-endpoint when this app restores that endpoint.
- **On every startup, before anything else**, the ledger is replayed: any endpoint still
  listed is restored to `priorMute` and cleared. This is what makes a hard-kill recoverable
  - the next launch repairs it without the user knowing anything was wrong.
- The app ships a `--restore` switch that replays and clears the ledger without starting
  the service, and the uninstaller invokes it. This is the uninstall path revision 1 lacked.

**Manual override.** Per the §9 decision, an external unmute of a *muted-by-us* endpoint is
treated as a manual claim (§7.4), not as drift to be corrected. The reconciler distinguishes
external changes from its own by comparing against the last value it wrote and ignoring
change notifications within 250 ms of its own `SetMute`, which prevents the self-feedback
loop this option is prone to.

### 7.4 Manual claim - hotkey and tray

- Global hotkey via `RegisterHotKey`, default `Ctrl+Alt+Shift+M`, configurable. If
  registration fails (already taken), surface a tray balloon **and** enter the `error`
  state - a silently dead hotkey on one machine is a one-sided, permanently non-functional
  control, and it is indistinguishable from normal operation until you need it.
- Semantics are **"claim this machine"**, not "toggle". Idempotent, and unambiguous no
  matter what state the pair is in.
- Three input surfaces all map to the same claim event: the hotkey, a tray left-click, and
  an external unmute of an endpoint we muted (§7.3). The third exists because reaching for
  the volume flyout is the natural reflex when a machine is unexpectedly silent, and
  revision 1 made that reflex fail.
- Tray icon states and **their producers**, since revision 1 listed an `error` state that
  nothing ever raised:

| State | Raised by |
|---|---|
| `active` | `activeOwner == self` |
| `muted` | §5.5 predicate true |
| `unclaimed` | `activeOwner == MachineId.None` and a peer is present - nobody has claimed yet, so both machines are audible *(revision 7)* |
| `alone` | no peer heartbeat within the presence window |
| `quarantine` | §7.6 rejoin window, state not yet reconciled |
| `error` | `activeOwner` outside the roster **and not `MachineId.None`**; roster incomplete after the pairing window; hotkey registration failure; `seq` bound exceeded, **on the wire or on disk**; unknown `v` from an **authenticated** peer; **sustained unverifiable traffic on our `pairId` with nothing valid accepted** (§7.1); ledger replay failure; endpoint enumeration failure; `config.json` unreadable on this profile; **a `pairKey` the configuration store refuses** (§7.5); **a persisted file whose `schema` this build does not recognise**; **a tunable field that failed validation and fell back to its default** |

`activeOwner == MachineId.None` is the ordinary pre-claim state of §5 and is **never** an
`error` cause. *(revision 7)* It gets its own state rather than being folded into `active`
or `alone`, because it is neither: `active` means "I hold the latch, so the peer is
silent", and `alone` means "there is no peer". Both are load-bearing diagnostics -
§7.4 exists because revision 1 shipped a tray that could not explain itself - and
overloading either would spend one of them to save an icon.

Note that in phase 1 this is **not** a transient. §8 ships phase 1 with mic detection not
built, so the only §5.1 writer is a manual claim: a freshly paired pair sits in `unclaimed`
indefinitely until somebody presses the hotkey. It is the first thing a user sees, on both
machines, and the one state with a specific action attached.

`error` precedence: where several causes hold at once, the tooltip names the **first raised**
and keeps it until acknowledged. Causes divide into two kinds, and they are acknowledged
differently. **Continuous** causes - `activeOwner` outside the roster, roster incomplete
after the pairing window, a cross-file `pairId` mismatch, an unreadable `config.json` or
`state.json`, a refused `pairKey`, an unrecognised `schema` - are true until the condition
itself changes and **cannot be acknowledged while they remain true**, because acknowledging
one would clear the tray while the machine sits in exactly the condition the `error` exists
to expose. **Edge** causes - hotkey registration failure, `seq` bound exceeded, unknown `v`,
sustained unverifiable traffic, ledger replay failure, endpoint enumeration failure, a
failed state write, a tunable that fell back to its default - latch until acknowledged.

*(revision 9)* Only one continuous cause - `activeOwner` outside the roster - is derivable
by the state machine from its own state. The rest are asserted by the component that owns
the condition, which means that component must also be able to **retract** them. Without a
retraction path a continuous cause raised from outside could never be cleared at all, since
acknowledgement is forbidden while it holds. An earlier implementation discarded every
continuous cause it had not derived itself, which silently swallowed this section's
cross-file mismatch and §7.7's incomplete roster - the exact silent tolerance §7.5 exists to
forbid.

- `error` is sticky until acknowledged and its tooltip names the specific cause. Because
  ownership is sticky, the tray is the only visible explanation for why a machine is
  silent; a generic error icon would be worse than none.
- **Denylist discovery.** The context menu exposes "Processes currently holding the
  microphone", listing live capture sessions with a one-click *add to denylist*. Without
  this, diagnosing an always-on mic consumer requires knowing the problem exists, knowing
  the process name, and hand-editing JSON.

### 7.5 StateStore - persistence

Both files live in the data root - `%LOCALAPPDATA%\SoloSpeaker` by default, overridable so
that two instances can run on one host for testing (§12). Both carry a `schema` integer and
the same `pairId`.

**`state.json`** - `(activeOwner, seq)` written on every change, atomically (temp file plus
`File.Replace`), and loaded at startup so a reboot does not reset arbitration.

```json
{ "schema": 1, "pairId": "b1f0...", "activeOwner": "2d81e4...", "seq": 41 }
```

**`config.json`** - the long-lived pairing artifacts and the tunables.

```json
{
  "schema": 1,
  "pairId": "b1f0...",
  "pairKeyProtected": "<base64>",
  "roster": ["7f3a9c...", "2d81e4..."],
  "port": 48292,
  "hotkey": "Ctrl+Alt+Shift+M",
  "denylist": [],
  "debounceSeconds": 5
}
```

`pairKeyProtected` is DPAPI-protected per [ADR 0013](adr/0013-dpapi-protects-pairkey-only.md);
every other field is plaintext, because this section's cross-file check and Goal 8's
hand-recoverability both assume the file can be inspected.

- **The roster lives in `config.json` while `activeOwner` lives in `state.json`**, and
  §5.5's invariant spans both. Restoring one from backup without the other breaks the
  invariant, so both files carry the same `pairId` and a mismatch raises `error` rather
  than being silently tolerated.
- **The pairing ceremony writes both files.** *(revision 9)* §7.7's `--pair-init` and
  `--pair-join` write `state.json` alongside `config.json`, with the new `pairId`,
  `activeOwner = MachineId.None`, and `seq = 0`. Two cases depend on it. Without it an
  absent `state.json` is indistinguishable from an ordinary first run, so §10's requirement
  to raise `error` when it is deleted and `config.json` survives cannot be met. And a
  `state.json` left behind by an uninstall that removed only `config.json` would carry the
  *old* `pairId`, so the cross-file check would fire on the first start after a successful
  pairing - an `error` on a correctly paired machine.
- **`seq` is bounded on disk.** *(revision 9)* A persisted value at or near `uint64.Max` is
  refused and raises `error`. §5.4 bounds `seq` in the datagram precisely because an
  unbounded one "poisons the persisted state on both machines"; enforcing that on the wire
  and not on the file leaves the same poisoning reachable through a hand-edit, a restore, or
  a half-written file.
- **Fields are validated according to what they gate.** *(revision 9)* `pairId`,
  `pairKeyProtected` and `roster` are **identity-bearing**: they are parsed strictly and a
  bad value refuses the whole document, because they are the right-hand side of §5.5's
  predicate and coercing them is how the invariant breaks. `port`, `hotkey`, `denylist` and
  `debounceSeconds` are **tunables**: a bad value falls back to the documented default and
  raises an `error` naming the field. The distinction matters because Goal 8 invites hand
  editing - refusing the whole document over a mistyped denylist entry would take the pair
  dark and, under ADR 0013's wording, tell the user to re-pair, which would not fix it.
- **`schema` distinguishes a newer file from a corrupt one.** *(revision 9)* A value this
  build does not recognise is its own outcome - "configuration is newer than this build" -
  and is **refused**, never migrated and never coerced. It is not an extension point; §8
  freezes the shape, so an unknown *field* remains a defect. What the integer buys is that
  Goal 8's hand-editor knows which shape they are looking at, and that §12's installer
  upgrading over an existing install can tell "older shape" from "damaged file".

### 7.6 Failsafes and rejoin

**Unmute paths.** Revision 1 asserted "unmute unconditionally on process exit, including
crash," and named finalizers plus `SystemEvents.SessionEnding` as the mechanism - neither
survives a hard kill, so the guarantee was not delivered. Restated honestly:

| Path | Mechanism |
|---|---|
| Graceful exit, logoff, shutdown | Restore endpoints, clear ledger, **send `bye` so the peer unmutes at once rather than after the presence window**. Sent on `WM_ENDSESSION`, never on the cancellable query phase |
| **Hard kill / crash / power loss** | **Not** recoverable in-process. Repaired by ledger replay on next startup (§7.3), and by `--restore` if the app is never launched again |
| Peer loss | §5.5 predicate goes false; unmute |
| App uninstalled | Uninstaller runs `--restore` **and aborts without deleting anything if that fails**, so the ledger outlives a failed repair |

**Rejoin quarantine.** A machine that has been asleep or powered off carries a persisted
`seq` that may exceed the peer's, even though its information is older in wall-clock terms
- a Lamport clock orders by event count, not time. Revision 1 therefore let opening a lid
move the mute away from the machine the user was actively using, violating Goal 5.

On cold start **and on resume from sleep** - the same code path; treating these separately
was a defect in the first attempt at this fix - the machine enters `quarantine`:

- It does **not** broadcast at all. *(revision 7)* §7.6 previously said only that it does
  not broadcast its *persisted ownership*, which left the send side undecided while
  `wire-format.md` rule 9 forbids omitting a field - so a datagram sent during the window
  had to carry some `(activeOwner, seq)`, and two of the three available answers reintroduce
  the defect this section exists to prevent. Sending `activeOwner = None` at the persisted
  `seq` lets a running peer adopt it under §5.4, so **opening a lid would erase ownership**;
  sending the persisted pair lets the peer adopt the stale higher `seq` and mute, which is
  the lid-open defect outright. Silence is the only answer that does neither. The peer sees
  the presence window lapse and unmutes, which is Goal 1's direction.

  Two consequences are accepted rather than hidden. The window "starts on first successful
  socket bind and send" below, so the first send is the pairing-mode or post-window one.
  And ADR 0011's roster completion, which rides on B's first ordinary heartbeat, is delayed
  by up to the window on B's cold start - harmless against a ten-minute pairing window, but
  it should be known rather than discovered.
- It does not apply mute.
- **Peer observation is a latch, not a live flag.** Any accepted non-`bye` datagram at any
  point in the window sets it, and nothing clears it for the remainder of the window - in
  particular a `bye` cannot. The latch, not the instantaneous presence state, is what is
  read at expiry.
- **The observed `(activeOwner, seq)` is recorded with the latch**, last-writer-wins.
  *(revision 7)* Without it there is nothing to adopt at expiry, and the value cannot be
  recovered by applying §5.4 as observations arrive: §10's row is "adopt peer's owner **even
  when our `seq` is higher**", which is exactly the case §5.4 says to ignore. Last-writer
  wins because a 12 s window at a 2 s beat admits six observations and the peer may
  legitimately claim within them; the latest observation is the peer's current state, which
  is the same reasoning the adopt rule itself rests on.
- **A restart preserves the latch and the observed pair.** *(revision 7)* A restart begins a
  new window, so "nothing clears it for the remainder of the window" did not settle this.
  Both answers have a failure mode: a surviving latch lets a flapping network carry a stale
  observation forward, while a cleared one means a Wi-Fi blip during a legitimate rejoin
  erases a correct observation, the machine expires "with no peer", and asserts its stale
  ownership - the lid-open defect reached through an ordinary network transition. Preserving
  fails toward deferring to the continuously-running machine, which is this section's stated
  rationale.
- If the latch is set, the peer's recorded `(activeOwner, seq)` is adopted **regardless of
  `seq` ordering**, and `seq` is advanced past it. The rationale is that a
  continuously-running machine's state reflects the most recent real events, while a
  rejoining machine's is stale by construction.
- If the window expires with the latch unset, the machine resumes normally from persisted
  state.

  The latch exists because presence is quarantine's expire-versus-adopt input, and
  revision 3 gave `bye` the power to clear presence without noticing what else read it. A
  laptop waking beside a desktop that then shuts down would observe the desktop's
  heartbeats, correctly defer to them - and then have that observation erased by the
  desktop's parting `bye`, expire "with no peer", and assert its own stale ownership. The
  desktop returns, sees the higher `seq`, adopts, and mutes. That is the lid-open defect
  this section exists to prevent, reached through an entirely ordinary shutdown with no
  attacker involved.
- **Live events are never suppressed.** A manual claim or a mic-edge claim during
  quarantine exits quarantine immediately and writes normally. Without this carve-out, a
  machine that boots directly into a meeting would mute itself mid-call - which inverts
  the design's highest-priority requirement in the name of fixing a lower-priority one.
- The window is 12s, and it starts on **first successful socket bind and send**, not on
  process start, because the network stack is routinely unavailable for several seconds
  after resume. It restarts on network-change notifications.

Simultaneous rejoin leaves both machines in quarantine, both audible, converging normally
when the window expires. Brief both-audible is permitted by Goal 3 and preferred by Goal 1.

### 7.7 Pairing ceremony

One-time, and it produces every long-lived artifact, which is why §8 places it in phase 1.

1. Machine A generates `pairId`, `pairKey`, and its own roster ID; displays them as a
   short code or QR.
2. Machine B generates its roster ID, and the two exchange roster IDs.
3. Both write `config.json` containing `pairId`, `pairKey`, and the complete two-entry
   roster - and `state.json` containing the same `pairId`, `activeOwner = MachineId.None`
   and `seq = 0`, per §7.5.

`pairKey` never crosses the network. Re-pairing is the supported path for replacing a
machine; §9 records that a replaced machine's stale roster entry is otherwise the exact
condition §5.5's positive predicate exists to catch.

The transfer mechanism for steps 1 and 2 was left open here and is settled in
[ADR 0011](adr/0011-pairing-bundle-file.md): a bundle file carries `pairId`, `pairKey`, and
A's roster ID to B, and B's roster ID returns to A on its ordinary first heartbeat, which
A accepts while its roster is incomplete and its bounded pairing window is open. A short
code was not viable - the three artifacts total 512 bits, around 103 base32 characters.

## 8. Implementation phases

Revision 1 drew its phase boundary *through* the wire format, the persisted schema, and the
pairing ceremony. Seven items that are free to change today would have become two-machine
migrations the moment phase 1 ran on both boxes - `pairKey` most sharply, since adding it
later forces a second hand-copy ceremony plus a mixed-version window whose symptom is
byte-identical to "the peer is switched off."

The boundary is now drawn **around** those artifacts. Everything that touches the wire, the
pairing ceremony, or persisted state lands in phase 1, whether or not its consumer exists
yet.

| Phase | Scope | Exit criteria |
|---|---|---|
| **1 - Foundation + manual claim** | State machine, roster, PeerLink with **final v1 wire format including `micLive` and `bye`**, `pairKey` + HMAC, pairing ceremony, StateStore, rejoin quarantine, MuteActuator **with ledger, `--restore`, and device-change handling**, tray with all states and producers, hotkey. Mic detection **not** built; `micLive` hardcoded `false`. | Claim on either machine mutes the other. Hard-killing the app and relaunching restores audio. Headset swap does not strand a muted endpoint. Uninstall restores audio, and refuses to delete anything if it cannot. A forged datagram without `pairKey` is dropped. A lid-open does not move the mute, and neither does a peer's departure. |
| **2 - Call detection** | MicWatcher, both signals (§7.2), denylist + discovery UI, debounce, `micLive` populated on the wire and in §5.5. | Joining a call on either machine takes ownership without touching the hotkey, and never mutes a machine that is capturing audio. |
| **3 - Deferred** | Bluetooth RSSI proximity, if LAN presence proves too coarse in practice. | Only on evidence from daily use. |

**Phase 1 ships with a known, recorded limitation**: it cannot evaluate requirement (1),
because nothing detects calls yet. Manual claim therefore covers cases it was never meant
to cover, and a machine in a meeting can be muted by a claim on the other machine. This is
an *accepted* limitation for a single-user, author-operated deployment, not an oversight -
pulling MicWatcher into phase 1 would collapse the one genuinely correct de-risking
decision in the original plan. It is recorded here so that phase 1's exit criteria are not
mistaken for "the product works."

Proximity is accessed through an `IProximitySource` interface from phase 1. Note that this
seam is narrower than it looks: PeerLink fuses presence, state replication, and
authentication, while an RSSI source supplies presence only. Phase 3 is therefore a
decomposition of PeerLink, not a drop-in swap, and the interface buys the presence boundary
only.

*(revision 7)* In phase 1 that interface is a **projection over reducer state**, not a
second authority. Presence cannot live wholly outside the reducer: §7.6's observation latch
is set by an accepted non-`bye` datagram - an event only the reducer sees - and it must
survive a `bye` clearing presence, so splitting presence across the seam would give one
fact two homes and let them disagree. The seam still buys what it was for, because the
phase-3 question "is the peer near enough to conflict with" is answered in one place and
read through one interface.

## 9. Decisions recorded, and open risks

### 9.1 Decisions taken in revision 2 - confirm or override

| # | Decision | Rationale | Reversible? |
|---|---|---|---|
| D-1 | An external unmute of an endpoint we muted is treated as a **manual claim**, not as drift to correct | Reaching for the volume flyout is the natural reflex; revision 1 made it fail. Self-feedback avoided per §7.3 | Yes, cheaply |
| D-2 | `selfMicLive` is **denylist-filtered but not debounced**; the claim edge is both | Safety wants breadth and speed, ownership wants narrowness and patience. Also removes the start-of-call audio outage | Yes |
| D-3 | "Mic in use" is accepted as the proxy for "in a call" | No meeting-app integration is in scope (§3). A participant muted *inside* the app still holds the capture session, so they stay protected; a participant who has released the mic entirely is not | Yes, but only by adding app integration |

D-1 is the one most worth a second look: it makes the volume flyout a third way to move
ownership, which is defensible as a form of manual claim but does widen §5.1's writer set
in spirit if not in letter.

### 9.2 Open risks

1. **LAN != proximity.** A laptop in another room on the same Wi-Fi counts as present and
   will be muted. Accepted for v1 by explicit decision; phase 3 exists if it bites.
   The *opposite* direction also exists and was unrecorded in revision 1: two machines
   physically adjacent but on different networks (one on Wi-Fi, one on a hotspot or
   Ethernet VLAN) will both stay unmuted.
2. **Always-on mic consumers make a machine permanently unmutable** under D-2, since the
   safety signal never clears. §7.4's discovery UI is the mitigation, but the failure is
   silent until noticed.
3. **§7.2's assumption that render-endpoint mute does not affect capture sessions** is
   load-bearing for the whole auto-claim loop and is unverified. It must be confirmed
   empirically before phase 2 is considered done, not assumed.
4. **Non-default render endpoints.** Only the default endpoint is muted; audio routed to a
   second device stays audible. Accepted.
5. **Unmute latency is asymmetric in the unsafe direction** - muting is immediate, but
   restoring audio after peer loss takes up to the 10s presence window. Silence therefore
   arrives fast and leaves slowly, which is the wrong way round given Goal 1. Bounded, not
   fixed. *Narrowed 2026-09-27 by the `bye` datagram (§7.1,
   [ADR 0016](adr/0016-goodbye-datagram-in-v1.md)), which removes the delay for deliberate
   shutdown. Crash, power loss, and out-of-range still fall back to the 10s timeout, so
   this risk stays on the register.*
6. **Per-user vs per-machine.** One interactive user assumed; fast-user-switching and RDP
   are not considered.
7. **N > 2 is unsupported.** The roster is fixed at two entries and the tiebreak assumes
   it. §3 lists this as a non-goal; it is now also a mechanism-level constraint.
8. **A sustained replay can hold a machine muted after its peer has gone.** *(revision 8)*
   Presence is refreshed by any accepted datagram, and a replayed one carries
   `activeOwner = <the peer>`, which satisfies §5.5 rather than being excluded by it. If the
   peer left **without** a `bye` - hard kill, power loss, out of range - a replay landing
   inside each presence window keeps this machine silent indefinitely. §7.1 previously
   asserted that §5.5's positive predicate bounded replay; it does not bound this case.
   Not fixable in band: a replay of the peer's latest datagram is byte-identical to a live
   beat, and `sentUtc` is deliberately not a drop condition. Mitigated by D-1 - an external
   unmute is a manual claim - and covered by a manual-matrix row rather than by CI, because
   only a real network can produce the condition.

## 10. Testing

The test plan tracks §9 and the review findings directly - revision 1's matrix exercised
none of its own risk register, and its single "hostile" row tested only attacks that would
have failed without any authentication at all.

**Unit** - the reducer is pure, so the whole arbitration model is testable in-process:

- call-start edge takes ownership; call-end does not release it
- claim on peer while peer absent, then peer returns -> peer is owner, we mute
- concurrent equal-`seq` edges converge to the same owner on both sides
- stale lower-`seq` datagram does not move ownership
- no peer -> never muted, whatever `activeOwner` says
- `activeOwner` outside the roster -> **both** machines audible, `error` raised
- `activeOwner == MachineId.None` -> **both** machines audible, and **no** `error`: this is
  the ordinary pre-claim state of §5, not a corrupt one
- `MachineId.None` is never accepted as a roster entry, from config, from a pairing bundle,
  or from §7.7 enrollment
- a machine's own datagram -> dropped silently at §7.1 step 3, and in particular **does not
  establish presence**; a machine whose peer has vanished goes `alone` rather than staying
  muted on the strength of its own heartbeats
- `activeOwner` differing only by case -> treated as outside the roster, not as a match
- `seq` delta beyond the bound -> dropped, `error` raised
- `micLive` absent from a datagram -> version mismatch, not `false`; rejected at §7.1 step 2
  as unverifiable, never at step 5, because rule 2 leaves the receiver nothing to reconstruct
- `bye` absent from a datagram -> version mismatch, not `false`; step 2 for the same reason
- sustained `pairId`-matching, `mac`-failing traffic with **nothing valid accepted** in the
  presence window -> `error`, cause "peer unverifiable"
- the same traffic **while valid datagrams are still arriving** -> no `error`; a public
  `pairId` must not let a stranger disable the tray
- unknown `v` from an authenticated peer -> `error`
- an accepted `bye` **bypasses §5.4 entirely** - it moves `activeOwner` in neither
  direction of `seq` ordering, including when its `seq` is strictly higher
- an accepted `bye` clears peer presence immediately, and presence is re-established only
  by the next accepted **non-`bye`** datagram
- a `bye` does **not** clear the §7.6 quarantine observation latch: a machine that observed
  its peer and then received a `bye` still adopts the peer's state at expiry
- a `bye` failing any ingress check is dropped like any other datagram
- `selfMicLive` true -> never muted, even when the peer is owner and present
- quarantine: peer observed -> adopt peer's owner even when our `seq` is higher
- quarantine: the observed `(activeOwner, seq)` is recorded last-writer-wins, so a peer that
  claims mid-window is adopted at its latest state rather than its first
- quarantine: a quarantined machine broadcasts **nothing**, so a rejoining machine carrying
  a higher `seq` cannot move the running peer's latch
- quarantine: a restart mid-window preserves the observation latch and the observed pair
- quarantine: manual claim and mic edge both exit quarantine and write normally
- a replayed state datagram after an accepted `bye` does **not** re-establish presence;
  only a datagram carrying a `(seq, activeOwner)` pair we have not already accepted does
- three unverifiable datagrams within a sliding presence window, with nothing valid
  accepted, raise `error`; two do not
- `activeOwner == MachineId.None` with a peer present -> `unclaimed`, not `active`, not
  `alone`, and not `error`
- every `TrayState` value has at least one producer, and the producers are mutually
  exclusive so the tray state is a total function of `(state, roster, now)`
- pairing mode enrolls an unknown `machineId` only while the roster is incomplete **and**
  the window is open, and only after the `mac` check passes (§7.7)
- pairing mode expiring with an incomplete roster -> `error`, and that machine never mutes

**Integration (two-machine manual matrix)** - claim ping-pong; call on each machine; call
ending leaves ownership unchanged; laptop walks away -> desktop unmutes; laptop returns ->
prior ownership reapplies; **lid-open does not move the mute**; both reboot simultaneously;
headset swap mid-mute; volume-flyout unmute is honoured as a claim (D-1); **graceful exit
unmutes the peer in under a second while a hard kill takes the full presence window**, and
the two are distinguishable in the log.

**Recovery** - hard-kill the process while muted, relaunch, confirm audio restored from the
ledger; hard-kill while muted and run `--restore` instead; uninstall while muted; delete
`state.json` but not `config.json` and confirm `error` rather than silent misbehaviour -
which is only distinguishable because §7.7's ceremony writes `state.json` too; a persisted
`seq` at `uint64.Max` is refused rather than adopted; a tunable field with a bad value falls
back to its default and names itself, while a bad `pairId`, `pairKeyProtected` or roster
entry refuses the whole document; a persisted file whose `schema` this build does not
recognise is refused rather than migrated; a second instance launched while the first holds
a mute exits without touching the ledger (§7.3); a `config.json` that cannot be decrypted on
this profile raises `error` with a re-pair cause rather than crashing; **`--restore` failing
leaves the binary and the ledger in place** rather than deleting them behind a mute it could
not repair.

**Hostile** - spoofed datagram with wrong `pairId`; correct `pairId` but no valid `mac`
(the case revision 1's HMAC could not actually have caught, since the key was derivable
from the broadcast); valid `mac` but `machineId` outside the roster; `seq = uint64.Max`;
a replayed valid `bye`, which unmutes - the safe direction - but must not move
`activeOwner`; a sustained replayed-`bye` flood during a rejoin window, which must not
prevent the quarantine latch from being honoured at expiry.

The full per-release checklist derived from the three manual blocks above is
[`manual-test-matrix.md`](manual-test-matrix.md), and the mapping from these unit rows to
test files is [`implementation-plan.md`](implementation-plan.md) §4.2.
