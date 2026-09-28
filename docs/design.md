# SoloSpeaker - Design Plan

**Status:** Revision 3 - adds the `bye` datagram and the naming change; revision 2 incorporated adversarial review findings DR-001...DR-006
**Author:** drafted with Copilot, 2026-09-25
**Target:** two Windows machines (one desktop, one laptop), single user

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
**roster**, and it is the complete domain of `activeOwner`.

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
- **Presence:** peer considered present if a valid datagram arrived within the last 10s
  (5 missed beats). Loss of presence unmutes; it does not alter `activeOwner`.
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
- **Ingress order:** drop on `pairId` mismatch -> drop on bad `mac` -> drop if `machineId`
  is not in the roster -> drop if `seq` delta exceeds the §5.4 bound. Only then process.
- **`micLive`** is always emitted explicitly, including in phase 1 where it is hardcoded
  `false`. An absent field must never be inferred as `false`; a datagram missing it is
  treated as a version mismatch and raises the `error` tray state.
- **`bye`** *(added 2026-09-27, see [ADR 0016](adr/0016-goodbye-datagram-in-v1.md))* marks
  a deliberate departure. It is sent on graceful exit, logoff, and shutdown - three times
  about 50 ms apart, since UDP offers no retry and no further heartbeat follows. A receiver
  that accepts it clears peer presence immediately instead of waiting out the 10s window,
  which collapses the §9.2-5 asymmetry for the common case. It **never** alters
  `activeOwner`; §5.2 holds, and this is a peer disappearing with better manners rather
  than a new writer. Like `micLive`, it is always emitted explicitly and an absent field is
  a version mismatch. A forged `bye` causes an unmute, which is the safe direction.
- **Clock skew:** `sentUtc` is informational and logged, but is **not** a drop condition.
  Revision 1 rejected datagrams more than 60s from local time, which bought nothing
  (ordering relies on `seq`) and could sever the pair entirely on clock drift.
- **Replay** within the presence window remains possible and is accepted. The blast radius
  is your own speaker, and §5.5's positive predicate bounds it.

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
| `alone` | no peer heartbeat within the presence window |
| `quarantine` | §7.6 rejoin window, state not yet reconciled |
| `error` | `activeOwner` outside the roster; hotkey registration failure; `seq` bound exceeded; missing `micLive` (version mismatch); ledger replay failure; endpoint enumeration failure |

- `error` is sticky until acknowledged and its tooltip names the specific cause. Because
  ownership is sticky, the tray is the only visible explanation for why a machine is
  silent; a generic error icon would be worse than none.
- **Denylist discovery.** The context menu exposes "Processes currently holding the
  microphone", listing live capture sessions with a one-click *add to denylist*. Without
  this, diagnosing an always-on mic consumer requires knowing the problem exists, knowing
  the process name, and hand-editing JSON.

### 7.5 StateStore - persistence

- `(activeOwner, seq)` written to `%LOCALAPPDATA%\SoloSpeaker\state.json` on every
  change, written atomically (temp file + `File.Replace`).
- Loaded at startup so a reboot doesn't reset arbitration.
- `config.json` alongside it holds `pairId`, `pairKey`, the roster, port, hotkey, denylist,
  and debounce.
- **The roster lives in `config.json` while `activeOwner` lives in `state.json`**, and
  §5.5's invariant spans both. Restoring one from backup without the other breaks the
  invariant, so both files carry the same `pairId` and a mismatch raises `error` rather
  than being silently tolerated.

### 7.6 Failsafes and rejoin

**Unmute paths.** Revision 1 asserted "unmute unconditionally on process exit, including
crash," and named finalizers plus `SystemEvents.SessionEnding` as the mechanism - neither
survives a hard kill, so the guarantee was not delivered. Restated honestly:

| Path | Mechanism |
|---|---|
| Graceful exit, logoff, shutdown | Restore endpoints, clear ledger, **send `bye` so the peer unmutes at once rather than after the presence window** |
| **Hard kill / crash / power loss** | **Not** recoverable in-process. Repaired by ledger replay on next startup (§7.3), and by `--restore` if the app is never launched again |
| Peer loss | §5.5 predicate goes false; unmute |
| App uninstalled | Uninstaller runs `--restore` |

**Rejoin quarantine.** A machine that has been asleep or powered off carries a persisted
`seq` that may exceed the peer's, even though its information is older in wall-clock terms
- a Lamport clock orders by event count, not time. Revision 1 therefore let opening a lid
move the mute away from the machine the user was actively using, violating Goal 5.

On cold start **and on resume from sleep** - the same code path; treating these separately
was a defect in the first attempt at this fix - the machine enters `quarantine`:

- It does **not** broadcast its persisted ownership, and does not apply mute.
- If the peer is observed during the window, the peer's `(activeOwner, seq)` is adopted
  **regardless of `seq` ordering**, and `seq` is advanced past it. The rationale is that a
  continuously-running machine's state reflects the most recent real events, while a
  rejoining machine's is stale by construction.
- If the window expires with no peer, the machine resumes normally from persisted state.
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
   roster.

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
| **1 - Foundation + manual claim** | State machine, roster, PeerLink with **final v1 wire format including `micLive`**, `pairKey` + HMAC, pairing ceremony, StateStore, rejoin quarantine, MuteActuator **with ledger, `--restore`, and device-change handling**, tray with all states and producers, hotkey. Mic detection **not** built; `micLive` hardcoded `false`. | Claim on either machine mutes the other. Hard-killing the app and relaunching restores audio. Headset swap does not strand a muted endpoint. Uninstall restores audio. A forged datagram without `pairKey` is dropped. A lid-open does not move the mute. |
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
- `activeOwner` differing only by case -> treated as outside the roster, not as a match
- `seq` delta beyond the bound -> dropped, `error` raised
- `micLive` absent from a datagram -> version mismatch, not `false`
- `bye` absent from a datagram -> version mismatch, not `false`
- an accepted `bye` clears peer presence immediately, and **leaves `activeOwner`
  untouched** - §5.2 holds, a departing peer never moves the latch
- a `bye` failing any ingress check is dropped like any other datagram
- `selfMicLive` true -> never muted, even when the peer is owner and present
- quarantine: peer observed -> adopt peer's owner even when our `seq` is higher
- quarantine: manual claim and mic edge both exit quarantine and write normally
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
`state.json` but not `config.json` and confirm `error` rather than silent misbehaviour; a
second instance launched while the first holds a mute exits without touching the ledger
(§7.3); a `config.json` that cannot be decrypted on this profile raises `error` with a
re-pair cause rather than crashing.

**Hostile** - spoofed datagram with wrong `pairId`; correct `pairId` but no valid `mac`
(the case revision 1's HMAC could not actually have caught, since the key was derivable
from the broadcast); valid `mac` but `machineId` outside the roster; `seq = uint64.Max`;
a replayed valid `bye`, which unmutes - the safe direction - but must not move
`activeOwner`.

The full per-release checklist derived from the three manual blocks above is
[`manual-test-matrix.md`](manual-test-matrix.md), and the mapping from these unit rows to
test files is [`implementation-plan.md`](implementation-plan.md) §4.2.
