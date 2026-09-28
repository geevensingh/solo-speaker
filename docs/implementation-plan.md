# SoloSpeaker — Implementation Plan

**Status:** Revision 1 — written against `design.md` revision 2
**Covers:** phase 1 only, plus the standing test and deployment machinery that phases 2
and 3 inherit
**Companions:** [`manual-test-matrix.md`](manual-test-matrix.md) ·
[`wire-format.md`](wire-format.md) · [`adr/`](adr/)

This plan answers three questions the design deliberately left alone: how the thing gets
built, how it gets tested, and how it gets onto the two machines. It does not restate the
design, and where the two disagree the design wins — except for the items in §10, which
are gaps the design does not cover at all.

---

## 1. Decisions this plan rests on

| Decision | Choice | ADR |
|---|---|---|
| Language and runtime | C# on .NET 10 (LTS to Nov 2028) | [0002](adr/0002-csharp-on-dotnet-10.md) |
| Packaging | Self-contained single-file exe + PowerShell install/uninstall | [0003](adr/0003-single-file-exe-packaging.md) |
| Tray UI | WinForms `NotifyIcon` | [0004](adr/0004-winforms-notifyicon-for-tray.md) |
| Win32 / COM interop | CsWin32 source generator | [0005](adr/0005-cswin32-for-wasapi-interop.md) |
| Test framework | xUnit, plus a two-node in-process harness | [0006](adr/0006-xunit-and-two-node-harness.md) |
| Product name | `SoloSpeaker` everywhere | [0007](adr/0007-solospeaker-naming.md) |
| Pairing transfer | Bundle file out, roster ID back on B's first heartbeat | [0011](adr/0011-pairing-bundle-file.md) |
| Second instance | Named mutex, taken before ledger replay | [0012](adr/0012-single-instance-guard.md) |
| `pairKey` at rest | DPAPI on that field only | [0013](adr/0013-dpapi-protects-pairkey-only.md) |
| Tray icons | Differentiated by silhouette | [0014](adr/0014-tray-icons-by-shape.md) |
| Diagnostics | Local rolling log, drops aggregated per minute | [0015](adr/0015-local-rolling-log.md) |
| Departure | `bye` field in wire format v1 | [0016](adr/0016-goodbye-datagram-in-v1.md) |

`design.md` §9.1's D-1, D-2, and D-3 are carried forward unchanged as
[0008](adr/0008-external-unmute-is-a-manual-claim.md),
[0009](adr/0009-safety-signal-is-not-debounced.md), and
[0010](adr/0010-mic-in-use-as-call-proxy.md).

---

## 2. Repository layout

```
SoloSpeaker.sln
Directory.Build.props          Shared compiler settings; InvariantGlobalization=true
global.json                    SDK pin
src/
  SoloSpeaker.Core/            net10.0 — pure. No Windows reference, by design.
    Abstractions/              The seams of §3
  SoloSpeaker.App/             net10.0-windows — tray host, interop, sockets, disk
    NativeMethods.txt          CsWin32 surface, annotated per design section
    app.manifest               asInvoker, PerMonitorV2
tests/
  SoloSpeaker.Core.Tests/      net10.0 — everything CI can prove
scripts/
  install.ps1                  Copy, unblock, register logon task
  uninstall.ps1                --restore, then remove
docs/
  design.md  implementation-plan.md  manual-test-matrix.md  wire-format.md  adr/
```

`SoloSpeaker.Core` targets `net10.0`, not `net10.0-windows`. That is the plan's single
most load-bearing structural choice: `design.md` §6 states that the state machine is a
pure reducer with all I/O at the edges, and a platform-neutral target framework turns that
statement into something the compiler enforces. A `using System.Windows.Forms` or a
WASAPI call cannot drift into the reducer, because the reference does not exist.

---

## 3. Testability seams

`design.md` §6 names the pure-reducer decision and §8 names `IProximitySource`, but the
remaining boundaries are implied rather than stated. They are declared in
`SoloSpeaker.Core/Abstractions/` so that the test strategy below is possible at all:

| Seam | Exists so that |
|---|---|
| `IClock` | Every interval is testable without sleeping — the 2 s cadence, 10 s presence window, 12 s quarantine, 5 s debounce, 250 ms self-change suppression |
| `IPeerTransport` | Datagrams can be dropped, duplicated, reordered and delayed on demand; validation sits *above* this seam so hostile-input tests run on real bytes without a socket |
| `IProximitySource` | Presence is never read from the transport directly, which is what would make the phase-3 decomposition expensive |
| `IMuteActuator` | Ledger and reconcile logic are testable without a real endpoint |
| `IMicWatcher` | Phase 1 supplies an always-`false` stub; phase 2 swaps in WASAPI with no change above the seam |
| `IStateStore` | Atomic-write and cross-file `pairId` checks are testable against a temp directory |

`IClock` carries a rule with teeth: nothing outside the composition root may call
`DateTime.UtcNow` or `Environment.TickCount64`. A single real-clock call inside the
reducer makes the timing tests either slow or flaky, and those are precisely the tests
covering the paths where Goal 1 is at risk.

---

## 4. Test strategy

### 4.1 What CI can prove, and what it cannot

This distinction matters more here than in most projects, because the parts of this design
most likely to be wrong are the parts a hosted runner cannot touch. A green CI badge on
this repository is a weaker claim than it looks, and should be read as such.

**CI can prove:** the reducer and every `design.md` §10 unit case; convergence and
quarantine across two in-process nodes; wire-format canonicalization, HMAC, and the full
ingress-order pipeline; hostile and fuzzed input; ledger replay and its failure modes;
atomic state writes; that the project builds warning-free and publishes to a working
single file.

**CI cannot prove:** that `IAudioEndpointVolume::SetMute` actually silences the machine;
that capture-session enumeration sees a real Teams or Zoom call; that UDP broadcast
crosses a real subnet; that resume-from-sleep restarts the quarantine window; that a
headset swap re-targets the endpoint; that `RegisterHotKey` conflicts are detected. Every
one of those lives in [`manual-test-matrix.md`](manual-test-matrix.md) and is signed off
by hand, per release, on both machines.

### 4.2 Unit tests — mapping `design.md` §10

Each row is one or more `[Fact]`/`[Theory]` cases. The design's list is adopted whole; the
final block is additional, and §4.6 explains why.

| `design.md` §10 case | Test file |
|---|---|
| Call-start edge takes ownership; call-end does not release it | `LatchTests` |
| Claim while peer absent, then peer returns → peer is owner, we mute | `LatchTests` |
| Concurrent equal-`seq` edges converge to the same owner on both sides | `ConvergenceTests` |
| Stale lower-`seq` datagram does not move ownership | `ConvergenceTests` |
| No peer → never muted, whatever `activeOwner` says | `MutePredicateTests` |
| `activeOwner` outside the roster → both audible, `error` raised | `MutePredicateTests` |
| `activeOwner` differing only by case → outside the roster, not a match | `RosterTests` |
| `seq` delta beyond the bound → dropped, `error` raised | `IngressTests` |
| `micLive` absent → version mismatch, not `false` | `IngressTests` |
| `selfMicLive` true → never muted, even with peer present and owner | `MutePredicateTests` |
| Quarantine: peer observed → adopt peer's owner even when our `seq` is higher | `QuarantineTests` |
| Quarantine: manual claim and mic edge both exit quarantine and write | `QuarantineTests` |

The case-sensitivity row deserves a note. `InvariantGlobalization=true` is set in
`Directory.Build.props` specifically to support it: §5.3 requires ordinal byte equality
with no casing, culture, or normalization semantics anywhere in the comparison path, and
invariant mode removes culture-sensitive behaviour from the runtime so that a
culture-sensitive comparison cannot be introduced by accident. The test still exists,
because the property is important enough to assert rather than infer.

### 4.3 The two-node in-process harness

The design files claim ping-pong, convergence, stale-`seq`, and quarantine adoption under
"integration (two-machine manual matrix)". Most of that does not need two machines, and
leaving it there means those paths are exercised a handful of times by hand rather than on
every commit.

The harness instantiates two reducers, two `IStateStore` instances over temp
directories, a shared `IClock` under test control, and a fake `IPeerTransport` that
can drop, duplicate, reorder, and delay datagrams between them. That converts the
following from manual rows into deterministic automated tests:

- claim ping-pong across an arbitrary number of alternations
- simultaneous claims at equal `seq`, asserting both sides pick the *same* winner
- one node asleep while the other advances, then rejoining — the lid-open case
- simultaneous rejoin: both quarantined, both audible, converging on expiry
- datagram loss up to and beyond the five-missed-beat presence window
- replayed datagrams inside the presence window, which §7.1 accepts — asserting the
  blast radius stays bounded rather than that the replay is prevented

What stays manual is everything downstream of the reducer: whether the machine actually
goes quiet.

### 4.4 Wire format — golden vectors and fuzzing

`design.md` §8 freezes the v1 wire format in phase 1 precisely so it never has to change
across two machines. That freeze needs enforcement, because a canonicalization change is
silent: it produces a valid-looking datagram that the peer rejects, and the symptom is
byte-identical to "the peer is switched off."

- **Golden vectors.** A checked-in set of byte-exact datagrams with their expected HMACs,
  in [`wire-format.md`](wire-format.md). Any change to field order, number formatting,
  or whitespace fails the test. Changing a vector is therefore a deliberate, reviewable
  act that forces the version question.
- **Ingress-order tests.** §7.1 fixes the order: `pairId` mismatch → bad `mac` →
  `machineId` outside roster → `seq` delta over bound. Each rejection reason is asserted
  independently, and asserted to fire *in that order*, because a datagram failing two
  checks must be attributed to the first.
- **Fuzzing.** §10's hostile block lists four cases. A property-based fuzzer over the
  parser is cheap and covers far more: truncation, oversized fields, duplicate keys,
  nested objects, non-UTF8 bytes, `seq` at boundaries. The invariant asserted is not
  "parses correctly" but "never throws, never mutates state, and never produces a mute" —
  which is the Goal 1 property restated as a test.

### 4.5 Ledger and crash recovery

`design.md` §7.3 makes the ledger the entire recovery story for a hard kill, so its
failure modes need to be tested rather than assumed. §10's recovery block covers the happy
path and the `state.json`-deleted case; these are added:

- ledger present and valid → endpoints restored to `priorMute`, entries cleared
- ledger describes a mute that never happened → restore is a no-op, entry cleared
- ledger is corrupt JSON → `error` raised, app still starts, nothing is muted
- ledger names an endpoint that no longer exists → entry cleared, no `error`
- ledger written but process killed before `SetMute` → next start is a clean no-op
- `state.json` temp file present but replace never happened → previous state intact
- `config.json` and `state.json` carry different `pairId` → `error`, not silent tolerance

### 4.6 Tests the design's §10 does not include

Added because the design states the behaviour but never asks anyone to check it, or
because an ADR in §1 introduced it:

- hotkey registration failure raises `error` (§7.4 requires this; §10 never tests it)
- a state change triggers an immediate extra broadcast, not just the next 2 s beat (§7.1)
- presence window boundary: valid at 10 s, absent at 10 s + ε
- `seq` bound boundary: delta of exactly 1000 accepted, 1001 dropped
- an endpoint change we make ourselves within 250 ms is *not* read as a manual claim —
  the self-feedback loop D-1 is prone to (§7.3)
- an external unmute *after* 250 ms *is* read as a manual claim
- every `TrayState` value has at least one producer, so §7.4's table cannot rot
- every `TrayState` value maps to a distinct icon resource (ADR 0014)
- an accepted `bye` clears presence immediately and **leaves `activeOwner` untouched**
  (ADR 0016) — the property that keeps §5.2 intact
- a `bye` failing any ingress check is dropped like any other datagram
- a datagram missing `bye` is a version mismatch, not `false`
- pairing mode enrolls an unknown `machineId` only while the roster is incomplete *and*
  the window is open, and only after the HMAC check passes (ADR 0011)
- pairing mode expiring with an incomplete roster raises `error`, and that machine can
  never mute
- a `config.json` whose `pairKeyProtected` cannot be decrypted raises `error` with a
  re-pair cause, rather than crashing or falling back (ADR 0013)
- ingress drops are aggregated per minute by reason rather than logged per datagram
  (ADR 0015) — asserted on a burst, because the failure mode is a log that floods

### 4.7 §9.2-3 — deferred to phase 2, by decision

`design.md` §9.2-3 records an unverified, load-bearing assumption: that muting the render
endpoint does not affect capture sessions. The whole auto-claim loop rests on it.

A standalone spike before phase 1 was considered and **declined**: it will be verified
against the running app in phase 2 instead, via manual matrix row H5.

That is a deliberate acceptance of a specific risk, so it is worth stating plainly rather
than burying. If the assumption turns out to be false, the discovery arrives after phase 1
has shipped to both machines, and the response is a phase-2 redesign of the claim path
rather than a phase-boundary adjustment made cheaply up front. Row H5 is therefore the
highest-value row in the matrix, and it should be run first among the phase-2 rows rather
than in listed order.

Nothing in phase 1 depends on the answer, which is what makes the deferral tenable.

---

## 5. Continuous integration

`.github/workflows/ci.yml`, on `windows-latest`, triggered by pushes and pull requests to
`main`.

| Step | Guards against |
|---|---|
| `dotnet format --verify-no-changes` | Style drift; keeps diffs about behaviour |
| `dotnet build -c Release` | `TreatWarningsAsErrors` is on repo-wide |
| `dotnet test` with TRX + coverage | The whole of §4.2 through §4.6 |
| `dotnet publish` single-file | Packaging regressions. Deployment is a hand-copied exe, so a broken publish is otherwise invisible until release day |
| Artifact upload of exe + scripts | Gives every commit an installable build, which is what makes the manual matrix cheap to run |
| PSScriptAnalyzer on `scripts/` | The install and uninstall scripts *are* the recovery path; they are linted, not trusted |

`windows-latest` is required rather than convenient: `SoloSpeaker.App` targets
`net10.0-windows`, and CsWin32 generates against the Windows SDK.

**What CI is not.** Work lands by direct push to `main`, so CI reports *after* the fact
rather than gating anything. That is a deliberate choice for a single-author repository,
but it changes what the badge means: a red `main` is possible, and nothing prevents it.
The `pull_request` trigger is kept anyway, so opening a PR gets pre-merge signal on the
occasions it is wanted.

Release readiness is therefore self-enforced: CI green on the commit being shipped, plus a
completed [`manual-test-matrix.md`](manual-test-matrix.md) run. Neither is mechanically
required, and both matter more here than usual — §4.1 is the reason.

---

## 6. Deployment

### 6.1 Build

```powershell
dotnet publish src\SoloSpeaker.App\SoloSpeaker.App.csproj -c Release -o artifacts\publish
```

Produces a single self-contained `SoloSpeaker.exe` (~49 MB, win-x64, no .NET runtime
prerequisite on either machine). Trimming is deliberately off — see the comment in
`SoloSpeaker.App.csproj`.

### 6.2 Install

`scripts\install.ps1` on each machine. It stops any running instance first, letting the
graceful-exit path restore audio and clear the ledger before the binary is replaced;
copies the exe to `%LOCALAPPDATA%\SoloSpeaker\bin`; clears the mark-of-the-web with
`Unblock-File`; and registers a logon task.

Two details are load-bearing:

- **Task Scheduler, not the `Run` key.** Startup ledger replay (§7.3) needs the audio
  endpoint enumerable, and a `Run`-key launch races the audio service at logon. A failed
  enumeration raises `error` (§7.4), so the cheap version of this choice produces an error
  icon on every boot. The task carries a 30 s delay for the same reason.
- **No elevation.** `app.manifest` requests `asInvoker`. Endpoint mute and `RegisterHotKey`
  are per-user, and an elevated process cannot receive hotkeys from a non-elevated
  foreground window — which would break manual claim on exactly the machine being used.

### 6.3 Pairing

Once, after installing on both. The ceremony produces `pairId`, `pairKey`, and the
two-entry roster (§7.7), by the mechanism settled in
[ADR 0011](adr/0011-pairing-bundle-file.md):

```powershell
# On the first machine
SoloSpeaker.exe --pair-init            # writes pairing.json, opens a 10-minute window

# Copy pairing.json to the second machine by any means, then
SoloSpeaker.exe --pair-join .\pairing.json
```

B's first ordinary heartbeat completes A's roster. Both machines then display an
8-hex-character fingerprint; compare them by eye. Both delete `pairing.json` — it is the
one moment `pairKey` exists outside the two configs.

Until both machines are paired, neither will ever mute: an unpaired machine has no
complete roster, so §5.5's positive predicate is false and both stay audible. That is the
correct behaviour and a useful property during rollout.

### 6.4 Update

The honest procedure for two machines under one owner is: **stop both, update both, start
both.** It avoids the mixed-version window entirely, and takes under a minute.

The window is worth naming because its symptom is misleading. If one machine runs a wire
version the other rejects, the rejecting machine simply sees no peer — which is
indistinguishable from "the peer is switched off," and per §5.5 leaves both audible. The
failure is therefore safe but silent, and easy to misdiagnose as a network problem.

The phase 1 → phase 2 upgrade is the payoff for the design's phase re-cut: `micLive` goes
from hardcoded `false` to a real value with no format change at all, because §7.1 insisted
on emitting the field from the start. That upgrade is wire-compatible in both directions,
so it is the one case where a staggered rollout is genuinely safe.

### 6.5 Uninstall and recovery

`scripts\uninstall.ps1` invokes `SoloSpeaker.exe --restore` **before** deleting the
binary. The order is the whole point: delete first and a muted endpoint is stranded with
nothing on disk able to repair it. The script reports loudly if `--restore` fails or the
exe is already missing, because the symptom it is covering for is a silent machine.

Recovery paths, in descending order of preference:

| Situation | Repair |
|---|---|
| Graceful exit, logoff, shutdown | Automatic; endpoints restored, ledger cleared |
| Hard kill, crash, power loss | Ledger replay on next start (§7.3) |
| App never launched again | `SoloSpeaker.exe --restore` |
| Binary deleted by hand | Unmute from the Windows volume mixer |

The last row is the real failsafe, and it is why option A's lack of an Add/Remove Programs
entry is an acceptable cost. `--restore` is a convenience; Goal 8 is satisfied by Windows
itself.

---

## 7. Phase 1 work breakdown

Ordered so that each step is verifiable when it lands, and so that the riskiest unknowns
are resolved first. Exit criteria are `design.md` §8's, unchanged.

| # | Work | Done when |
|---|---|---|
| 1 | `MachineId`, `Roster`, ordinal comparison | `RosterTests` green, including the case-only mismatch |
| 2 | Wire format v1: canonicalization, HMAC, parse, ingress order, **`bye`** | Golden vectors + fuzzer green; `wire-format.md` filled in |
| 3 | `StateMachine` reducer | All of §4.2 green |
| 4 | Two-node in-process harness | All of §4.3 green |
| 5 | `StateStore` + config, atomic write, cross-file `pairId` check, **DPAPI on `pairKey`** | §4.5's state rows green; a config from another profile raises `error`, not a crash |
| 6 | `PeerLink` over real UDP, including `bye` on graceful exit | Two instances on one host, separate config roots and ports, exchange state |
| 7 | `MuteActuator` + ledger + `--restore` + `IMMNotificationClient` | §4.5's ledger rows green; headset swap re-targets by hand |
| 8 | **Logging** (ADR 0015), with per-minute ingress-drop aggregation | Ownership changes name their source; a version mismatch is distinguishable from an absent peer |
| 9 | Tray: five states, **icons** (ADR 0014), named producers, hotkey, sticky `error` | Every state reachable and observed; registration failure raises `error` |
| 10 | Pairing ceremony (ADR 0011), including pairing-mode ingress exception | Two machines paired from scratch; fingerprints match |
| 11 | Quarantine: cold start, resume, network-change restart | Lid-open does not move the mute |
| 12 | **Single-instance guard** (ADR 0012), then packaging: publish, install, uninstall, logon task | Second launch exits without touching the ledger; clean install → reboot → still working → clean uninstall |
| 13 | Manual matrix sign-off | [`manual-test-matrix.md`](manual-test-matrix.md) fully signed |

Step 6 is worth a note: a good deal of PeerLink can be exercised on one machine by running
two instances with separate config roots and ports. It does not cover subnet broadcast
behaviour, but it covers the pipeline above the socket. Note that step 12's guard uses a
single named mutex, so the two-instance technique needs the guard scoped by config root
rather than by machine — decide that when step 12 lands, not before.

Step 8 comes before the tray deliberately. Bringing up five icon states without a log is
harder than it needs to be, and the log is what makes step 10's pairing failures legible.

## 8. Open items — all resolved

Six gaps in `design.md` were identified while writing this plan. All are now settled by
ADRs, and the resolutions are folded into §7's work breakdown above.

| Was | Resolution | ADR |
|---|---|---|
| §8.1 Pairing transfer unspecified | Bundle file to B; B's roster ID returns on its ordinary first heartbeat, accepted while A's roster is incomplete and its pairing window is open. **No new wire message type** | [0011](adr/0011-pairing-bundle-file.md) |
| §8.2 No single-instance guard | Named mutex `Local\SoloSpeaker`, acquired **before** ledger replay | [0012](adr/0012-single-instance-guard.md) |
| §8.3 `pairKey` in plaintext | DPAPI protects the `pairKey` field only; the rest of `config.json` stays readable | [0013](adr/0013-dpapi-protects-pairkey-only.md) |
| §8.4 No tray icon assets | Five icons differentiated by silhouette, colour as reinforcement only | [0014](adr/0014-tray-icons-by-shape.md) |
| §8.5 No diagnostics | Rolling local log; **ingress drops aggregated per minute by reason** | [0015](adr/0015-local-rolling-log.md) |
| §8.6 Asymmetric unmute latency | `bye` field added to wire format v1 | [0016](adr/0016-goodbye-datagram-in-v1.md) |

Three of these turned out to be sharper than they first looked, and the reasoning is worth
keeping visible:

**§8.6 was a now-or-never decision, not an optional improvement.** `design.md` §8 freezes
the wire format in phase 1 so that nothing on the wire becomes a two-machine migration. A
`bye` field added later costs exactly the coordinated update that phase re-cut exists to
prevent. Filing it as "nice to have" would have quietly converted it into "never, without
a version bump".

**§8.1 needed no new wire message.** The obvious design — a dedicated pairing datagram —
would itself have had to be frozen into v1. Reusing B's ordinary heartbeat, with a bounded
pairing-mode exception to ingress step 3, avoids adding anything to a format that is about
to be frozen. Authentication is unaffected: the HMAC check still runs first, so enrollment
still requires possession of `pairKey`.

**§8.2's ordering is the substance.** A second instance is not merely redundant. Starting
while the first holds a legitimate mute, it would replay the ledger, restore the endpoint,
and clear the entry — leaving the first instance holding a mute whose recovery record no
longer exists. A hard kill after that strands the endpoint permanently. The guard must
precede ledger replay, which slightly qualifies §7.3's "before anything else".

`design.md` §9.2's risk register is otherwise unchanged. §9.2-3 — whether render-mute
disturbs capture sessions — remains open by decision: it will be verified against the
running app in phase 2 rather than by a standalone spike.
