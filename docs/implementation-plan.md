# SoloSpeaker - Implementation Plan

**Status:** Revision 4 - written against `design.md` revision 8
**Covers:** phase 1 only, plus the standing test and deployment machinery that phases 2
and 3 inherit
**Companions:** [`manual-test-matrix.md`](manual-test-matrix.md) -
[`wire-format.md`](wire-format.md) - [`review-2026-09-28.md`](review-2026-09-28.md) -
[`adr/`](adr/)

> The four blocking findings of [`review-2026-09-28.md`](review-2026-09-28.md) are
> **fixed**, and issues #1 and #2 are closed. `design.md` is at revision 5: the `bye`
> semantics are corrected, §7.1 owns ingress outright with a version-mismatch producer that
> can actually fire, the uninstall script aborts rather than deleting behind a failed
> repair, and the seam set is redrawn so §4.1's claims about what CI can prove are now
> true. Issues #3 through #7 remain open.

This plan answers three questions the design deliberately left alone: how the thing gets
built, how it gets tested, and how it gets onto the two machines. It does not restate the
design, and where the two disagree the design wins - including for ingress, which
`design.md` §7.1 owns outright.

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
  SoloSpeaker.Core/            net10.0 - pure. No Windows reference, by design.
    Abstractions/              The seams of §3
    Identity/                  MachineId, PairId, Roster - value types, not a component
    PeerLink/                  Ingress (§7.1), with Wire/ for the frozen byte form
    StateMachine/              The pure reducer of §6
    StateStore/                §7.5's two persisted files, over IFileStore
    Persistence/               JsonPersistence - the one convention all three files share
    Composition/               The deterministic cycle that binds them. See §4.3
  SoloSpeaker.App/             net10.0-windows - tray host, interop, sockets, disk
    Hosting/                   Data root, startup ordering - the host's territory per AGENTS.md §3
    NativeMethods.txt          CsWin32 surface, annotated per design section
    app.manifest               asInvoker, PerMonitorV2
tests/
  SoloSpeaker.Core.Tests/      net10.0 - the reducer, wire format, ledger, config
  SoloSpeaker.App.Tests/       net10.0-windows - DPAPI, WASAPI, icon resources
scripts/
  install.ps1                  Copy, unblock, register logon task
  uninstall.ps1                --restore, then remove - and abort if it fails
  Test-Ascii.ps1               AGENTS.md section 4 lint gate
docs/
  design.md  implementation-plan.md  manual-test-matrix.md  wire-format.md
  review-2026-09-28.md  adr/
```

`SoloSpeaker.Core` targets `net10.0`, not `net10.0-windows`. That is the plan's single
most load-bearing structural choice: `design.md` §6 states that the state machine is a
pure reducer with all I/O at the edges, and a platform-neutral target framework turns that
statement into something the compiler enforces. A `using System.Windows.Forms` or a
WASAPI call cannot drift into the reducer, because the reference does not exist.

Note the honest bound, established by the review: it prevents referencing Windows types.
It does **not** prevent `DateTime.UtcNow`, `File.WriteAllText`, `Socket`, or `Random` -
the impurities that actually threaten the timing tests. Those are held by the `IClock`
rule in `AGENTS.md` §3, which is discipline rather than mechanism.

---

## 3. Testability seams

`design.md` §6 names the pure-reducer decision and §8 names `IProximitySource`, but the
remaining boundaries are implied rather than stated. They are declared in
`SoloSpeaker.Core/Abstractions/` so that the test strategy below is possible at all:

| Seam | Exists so that |
|---|---|
| `IClock` | Every interval is testable without sleeping - the 2 s cadence, 10 s presence window, 12 s quarantine, 5 s debounce, 250 ms self-change suppression |
| `IPeerTransport` | Datagrams can be dropped, duplicated, reordered and delayed on demand; validation sits *above* this seam so hostile-input tests run on real bytes without a socket |
| `IProximitySource` | Presence is never read from the transport directly, which is what would make the phase-3 decomposition expensive. In phase 1 it is a **projection over reducer state**, not a second authority - see `design.md` §8. Presence cannot live wholly outside the reducer, because §7.6's observation latch is set by an accepted non-`bye` datagram and must survive a `bye` clearing presence, so a split would give one fact two homes |
| `IMuteActuator` | Actuation is testable without a real endpoint. **Actuation only** - the ledger is separate |
| `ILedger` | §7.3's recovery record is a distinct component per §6, and its replay policy lives in Core where tests can reach it |
| `IFileStore` | Persisted-file *policy* lives in Core over a thin I/O seam, rather than in App where no test could reference it |
| `ILogSink` | Append-only rolling diagnostics need the opposite of `IFileStore`'s atomic write-and-replace; routing one log line through `IFileStore` would rewrite the whole file. The sink buffers and never writes from the reduce cycle, because that cycle runs on the same thread that handles `WM_ENDSESSION`, so a stalled disk write cannot delay the departure datagram, the endpoint restore, or the ledger clear |
| `IConfigStore` | The roster has a seam at all. It is the right-hand side of §5.5's predicate and previously had none |
| `ISecretProtector` | DPAPI is Windows-only; without this seam one call would drag the whole config store back into App |
| `IMicWatcher` | Phase 1 supplies an always-`false` stub; phase 2 swaps in WASAPI with no change above the seam |
| `IStateStore` | Atomic-write and the cross-file `pairId` check are testable against a fake file store |

`IClock` carries a rule with teeth: nothing outside the composition root may call
`DateTime.UtcNow` or `Environment.TickCount64`. A single real-clock call inside the
reducer makes the timing tests either slow or flaky, and those are precisely the tests
covering the paths where Goal 1 is at risk.

The last four seams are the result of `review-2026-09-28.md` finding B-4. An earlier
version of this section claimed `IStateStore` existed so "cross-file `pairId` checks are
testable", while the interface exposed no config surface to express the check with - and
fused `MuteActuator` with `Ledger`, which §6 and `AGENTS.md` §3 both name separately.

---

## 4. Test strategy

### 4.1 What CI can prove, and what it cannot

This distinction matters more here than in most projects, because the parts of this design
most likely to be wrong are the parts a hosted runner cannot touch. A green CI badge on
this repository is a weaker claim than it looks, and should be read as such.

**CI can prove:** the reducer and every `design.md` §10 unit case; convergence and
quarantine across two in-process nodes; wire-format canonicalization, HMAC, and the full
ingress-order pipeline; hostile and fuzzed input; ledger replay and its failure modes;
atomic state writes; the cross-file `pairId` check; that the project builds warning-free
and publishes to a working single file.

Ledger and config cases are provable only because their policy lives in
`SoloSpeaker.Core` over `IFileStore`. That was not true when this section was first
written - see `review-2026-09-28.md` finding B-4 - and the claim is worth re-checking
rather than trusting if the seam ever moves.

**CI cannot prove:** that `IAudioEndpointVolume::SetMute` actually silences the machine;
that capture-session enumeration sees a real Teams or Zoom call; that UDP broadcast
crosses a real subnet; that resume-from-sleep produces the real bind or network-change
edge that restarts the quarantine window; that a headset swap re-targets the endpoint; that
`RegisterHotKey` conflicts are detected. Every one of those lives in
[`manual-test-matrix.md`](manual-test-matrix.md) and is signed off by hand, per release, on
both machines.

Two more belong on that list, and they are easy to miss because the two-node harness looks
like it covers them. **Clock skew between the machines** is unrepresentable in a harness
driven by one controlled clock - which is sound, because the reducer never compares clocks
and `sentUtc` is not a drop condition, but it means the harness demonstrates nothing about
skew. Quarantine entry itself is no longer on this list: moving it into Core's
`StartupDecision` means the harness can observe that startup hands the host an already-open
window. The remaining socket edge is **bind-triggered restart**: §7.6 re-measures the
window on first successful socket bind, and the harness raises `QuarantineRestarted`
directly rather than observing a real bind. That restart remains a real-network property.

A third, from work item 5: **that a `config.json` protected under a different Windows user
profile fails to decrypt**. A test process runs as one profile and cannot protect data as
another, so `App.Tests` proves only that the mechanism fails closed - a wrong entropy and a
corrupted ciphertext both return `false` rather than throwing, which is the same route DPAPI
takes for a wrong profile. The profile case itself is manual row **E10**.

### 4.2 Unit tests - mapping `design.md` §10

Each row is one or more `[Fact]`/`[Theory]` cases. The design's list is adopted whole; the
final block is additional, and §4.6 explains why.

| `design.md` §10 case | Test file |
|---|---|
| Call-start edge takes ownership; call-end does not release it | `LatchTests` |
| Claim while peer absent, then peer returns -> peer is owner, we mute | `LatchTests` |
| Concurrent equal-`seq` edges converge to the same owner on both sides | `ConvergenceTests` |
| Stale lower-`seq` datagram does not move ownership | `ConvergenceTests` |
| No peer -> never muted, whatever `activeOwner` says | `MutePredicateTests` |
| `activeOwner` outside the roster -> both audible, `error` raised | `MutePredicateTests` |
| `activeOwner` differing only by case -> outside the roster, not a match | `RosterTests` |
| `activeOwner == MachineId.None` with a peer present -> `unclaimed` | `TrayStateTests` |
| Every `TrayState` value has a producer, and the producers are mutually exclusive | `TrayStateTests` |
| Three unverifiable datagrams with nothing valid accepted -> `error`; two do not | `UnverifiablePeerTests` |
| A replayed state datagram after a `bye` does not re-establish presence | `PresenceTests` |
| `seq` delta beyond the bound -> dropped, `error` raised | `IngressTests` |
| `micLive` absent -> version mismatch, not `false` | `IngressTests` |
| `selfMicLive` true -> never muted, even with peer present and owner | `MutePredicateTests` |
| Quarantine: peer observed -> adopt peer's owner even when our `seq` is higher | `QuarantineTests` |
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

The harness instantiates two reducers, two `IStateStore` fakes that round-trip through the
same canonical rendering the real store will write, a shared `IClock` under test control,
and a fake `IPeerTransport` that can drop, duplicate, reorder, and delay datagrams. Two
properties of that transport are load-bearing rather than incidental:

- **It is a broadcast medium, not a pipe.** Every send is delivered to *both* subscribers
  **including the sender**, because that is what an IPv4 subnet broadcast does. A
  point-to-point fake would make `IngressResult.SelfOrigin` unreachable inside the loop, and
  the permanent-mute failure design §7.1's self-origin paragraph exists to prevent would be
  structurally untestable in the harness built to find it.
- **Delivery is queued, never inline.** `DatagramReceived` is a push event, so a synchronous
  send would re-enter the reducer while the outer reduction is still on the stack.

The store is a fake rather than a real file-backed one because row 5 is what builds the real
store; row 5's done-criterion gains a restart scenario that runs this harness over it. The
round-tripping requirement is what stops the substitution from deleting coverage.

That converts the following from manual rows into deterministic automated tests:

| § 4.3 scenario | Test |
|---|---|
| Claim ping-pong across an arbitrary number of alternations | `TwoNodeTests.Claim_ping_pong_converges_on_every_alternation` |
| Simultaneous claims at equal `seq`, asserting both sides pick the *same* winner | `TwoNodeTests.Simultaneous_claims_converge_on_the_lexicographically_smaller_id` |
| One node asleep while the other advances, then rejoining - the lid-open case | `TwoNodeTests.A_rejoining_node_defers_to_the_one_that_kept_running` |
| Simultaneous rejoin: both quarantined, both audible, converging on expiry | `TwoNodeTests.Simultaneous_rejoin_leaves_both_audible_then_converges` |
| Datagram loss up to and beyond the five-missed-beat presence window | `TwoNodeTests.Presence_survives_four_missed_beats_and_lapses_after_five` |
| Replayed datagrams inside the presence window | `TwoNodeTests.A_replay_moves_no_ownership_and_persists_nothing` |

The simultaneous-claim row needs care that the scenario text does not convey: if the harness
delivers between the two nodes' steps, the second node adopts the first's claim, §5.4's
tiebreak is never reached, and the two still agree - so the stated assertion passes while the
code it exists to cover never runs. The test holds both datagrams in flight, releases them
together, and asserts the winner's **identity** and the resulting `seq`, not merely that the
two sides match.

The replay row is the one whose expectation changed. Earlier revisions asked it to assert
that "the blast radius stays bounded"; design revision 8 establishes that presence is **not**
bounded when the peer left without a `bye` - see §9.2-8. The test asserts the four properties
that do hold - a replay moves no ownership, advances no `seq`, persists nothing, and cannot
re-establish presence after a `bye` - and the unbounded case is a manual-matrix row (F8),
because only a real network produces it.

What stays manual is everything downstream of the reducer: whether the machine actually
goes quiet.

What stays manual is everything downstream of the reducer: whether the machine actually
goes quiet.

### 4.4 Wire format - golden vectors and fuzzing

`design.md` §8 freezes the v1 wire format in phase 1 precisely so it never has to change
across two machines. That freeze needs enforcement, because a canonicalization change is
silent: it produces a valid-looking datagram that the peer rejects, and the symptom is
byte-identical to "the peer is switched off."

- **Golden vectors.** A checked-in set of byte-exact datagrams with their expected HMACs,
  in [`wire-format.md`](wire-format.md). Any change to field order, number formatting,
  or whitespace fails the test. Changing a vector is therefore a deliberate, reviewable
  act that forces the version question.
- **Ingress-order tests.** `design.md` §7.1 fixes the order: `pairId` mismatch -> bad
  `mac` -> `machineId` outside roster -> `seq` delta over bound -> unknown `v` or missing
  field. Each rejection reason is asserted independently, and asserted to fire *in that
  order*, because a datagram failing two checks must be attributed to the first.
- **The unverifiable-peer producer.** §7.1 raises `error` on sustained `pairId`-matching,
  `mac`-failing traffic *while nothing valid is being accepted*. Both halves are tested:
  the producer fires when the peer has genuinely gone silent, and does **not** fire while
  valid datagrams are still arriving - because `pairId` is public and a stranger must not
  be able to disable the tray.
- **Fuzzing.** §10's hostile block lists four cases. A property-based fuzzer over the
  parser is cheap and covers far more: truncation, oversized fields, duplicate keys,
  nested objects, non-UTF8 bytes, `seq` at boundaries. The invariant asserted is not
  "parses correctly" but "never throws, never mutates state, and never produces a mute" -
  which is the Goal 1 property restated as a test.

### 4.5 Ledger and crash recovery

`design.md` §7.3 makes the ledger the entire recovery story for a hard kill, so its
failure modes need to be tested rather than assumed. §10's recovery block covers the happy
path and the `state.json`-deleted case; these are added:

- ledger present and valid, and restores succeed -> `priorMute: false` entries are
  unmuted, `priorMute: true` entries are logged and cleared without a mute write, and all
  entries are cleared
- ledger describes a mute that never happened -> restore is a no-op, entry cleared
- ledger is corrupt JSON -> `error` raised, app still starts, nothing is muted
- ledger names an endpoint that no longer exists -> entry cleared, no `error`
- ledger names an endpoint that exists but whose restore fails -> the entry is retained,
  `--restore` exits non-zero, and `error` is raised
- ledger written but process killed before `SetMute` -> next start is a clean no-op
- `state.json` temp file present but replace never happened -> previous state intact
- `config.json` and `state.json` carry different `pairId` -> `error`, not silent tolerance
- `state.json` absent on a **configured** machine -> `error`, because §7.7's ceremony writes
  it; absent with no `config.json` either -> an ordinary first run
- a persisted `seq` beyond the §5.4 bound -> refused, not adopted
- a persisted `schema` this build does not recognise -> refused, never migrated
- an identity-bearing field (`pairId`, `pairKeyProtected`, `roster`) that fails the strict
  parse -> the whole document refused
- a tunable (`port`, `hotkey`, `denylist`, `debounceSeconds`) that fails validation -> the
  documented default, plus an `error` naming the field
- completing the roster rewrites `config.json` **without dropping** the tunables
- a node torn down and rebuilt from its own persisted state through the real store resumes
  the latch it wrote, and is audible until its peer is heard again

### 4.6 Tests the design's §10 does not include

Added because the design states the behaviour but never asks anyone to check it, or
because an ADR in §1 introduced it:

- hotkey registration failure raises `error` (§7.4 requires this; §10 never tests it)
- a state change triggers an immediate extra broadcast, not just the next 2 s beat (§7.1)
- presence window boundary: valid at 10 s, absent at 10 s + epsilon
- `seq` bound boundary: delta of exactly 1000 accepted, 1001 dropped
- an endpoint change we make ourselves within 250 ms is *not* read as a manual claim -
  the self-feedback loop D-1 is prone to (§7.3)
- an external unmute *after* 250 ms *is* read as a manual claim
- every `TrayState` value has at least one producer, so §7.4's table cannot rot
- every `TrayState` value maps to a distinct icon resource (ADR 0014)
- an accepted `bye` clears presence immediately and **leaves `activeOwner` untouched**
  (ADR 0016) - the property that keeps §5.2 intact
- a `bye` failing any ingress check is dropped like any other datagram
- a datagram missing `bye` is a version mismatch, not `false`
- pairing mode enrolls an unknown `machineId` only while the roster is incomplete *and*
  the window is open, and only after the HMAC check passes (ADR 0011)
- pairing mode expiring with an incomplete roster raises `error`, and that machine can
  never mute
- a `config.json` whose `pairKeyProtected` cannot be decrypted raises `error` with a
  re-pair cause, rather than crashing or falling back (ADR 0013)
- ingress drops are aggregated per minute by reason rather than logged per datagram
  (ADR 0015) - asserted on a burst, because the failure mode is a log that floods

### 4.7 §9.2-3 - deferred to phase 2, by decision

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
required, and both matter more here than usual - §4.1 is the reason.

---

## 6. Deployment

### 6.1 Build

```powershell
dotnet publish src\SoloSpeaker.App\SoloSpeaker.App.csproj -c Release -o artifacts\publish
```

Produces a single self-contained `SoloSpeaker.exe` (~49 MB, win-x64, no .NET runtime
prerequisite on either machine). Trimming is deliberately off - see the comment in
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
  foreground window - which would break manual claim on exactly the machine being used.

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
8-hex-character fingerprint; compare them by eye. Both delete `pairing.json` - it is the
one moment `pairKey` exists outside the two configs.

Until both machines are paired, neither will ever mute: an unpaired machine has no
complete roster, so §5.5's positive predicate is false and both stay audible. That is the
correct behaviour and a useful property during rollout.

### 6.4 Update

The honest procedure for two machines under one owner is: **stop both, update both, start
both.** It avoids the mixed-version window entirely, and takes under a minute.

The window is worth naming because its symptom is misleading. If one machine runs a wire
version the other rejects, the rejecting machine simply sees no peer - which is
indistinguishable from "the peer is switched off," and per §5.5 leaves both audible. The
failure is therefore safe but silent, and easy to misdiagnose as a network problem.

The phase 1 -> phase 2 upgrade is the payoff for the design's phase re-cut: `micLive` goes
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
| 3 | `StateMachine` reducer | All of §4.2 green, **plus §4.4's unverifiable-peer producer**. §4.2 has no row for that producer and row 2 structurally could not implement it, so without this clause row 3 would land a §7.4 producer with no completion test - which is §7.4's original defect |
| 4 | Two-node in-process harness | All of §4.3 green |
| 5 | `StateStore` + `ConfigStore` over `IFileStore`, atomic write, cross-file `pairId` check, **DPAPI `ISecretProtector`** | §4.5's state rows green in Core, including the restart scenario over the real store. `App.Tests` proves DPAPI **fails closed** - a wrong entropy and a corrupted ciphertext both return `false` rather than throwing. The cross-profile case itself is manual row E10: a test process cannot protect data as another Windows profile, and DPAPI surfaces both through the same failure route |
| 6 | `PeerLink` over real UDP, including `bye` on graceful exit and its three receipt rules | Two instances on one host, separate config roots, and one port with `SO_REUSEADDR`, exchange state; a `bye` clears presence without moving `activeOwner` |
| 7 | `MuteActuator` + `Ledger` over `IFileStore` + `--restore` + `IMMNotificationClient` + mutex acquisition | §4.5's ledger rows green in Core; headset swap re-targets by hand; same-root launch and `--restore` take the guard before ledger replay |
| 8 | **Logging** (ADR 0015), with per-minute ingress-drop aggregation | Ownership changes name their source; a version mismatch is distinguishable from an absent peer |
| 9 | Tray: six states, **icons** (ADR 0014), named producers, hotkey, sticky `error` | Every state reachable and observed; registration failure raises `error` |
| 10 | Pairing ceremony (ADR 0011), including pairing-mode ingress exception | Two machines paired from scratch; fingerprints match |
| 11 | Quarantine: cold start, resume, network-change restart | Lid-open does not move the mute |
| 12 | **Single-instance UX** (ADR 0012), a real shutdown channel so the scripts can stop a tray app, then packaging: publish, install, uninstall, logon task | Second launch reports visibly; `install.ps1` upgrades over a running instance without throwing; clean install -> reboot -> still working -> clean uninstall |
| 13 | Manual matrix sign-off | [`manual-test-matrix.md`](manual-test-matrix.md) fully signed |

Step 6 is worth a note: a good deal of PeerLink can be exercised on one machine by running
two instances with separate config roots on the same configured port, with both sockets
bound using `SO_REUSEADDR`. That is the same addressing model as two machines on a subnet:
one broadcast destination, multiple listeners. It also covers broadcast delivery and the
self-origin drop, because Windows delivers a subnet broadcast to both local sockets,
including the sender's own. Separate roots still matter: they carry identity, and without
them both instances share a roster self-entry and every datagram is discarded as
self-origin, so the test would pass vacuously. Work item 6 ships the mutex name derivation
needed to keep this technique alive; work item 12 still ships acquisition, the tray
balloon, the exit code, and the shutdown channel.

Step 8 comes before the tray deliberately. Bringing up five icon states without a log is
harder than it needs to be, and the log is what makes step 10's pairing failures legible.

## 8. Open items - mechanisms chosen

Six gaps in `design.md` were identified while writing this plan, and a mechanism is chosen
for each. **"Mechanism chosen" is not "resolved."** An earlier revision of this section was
titled "all resolved"; the adversarial review of 2026-09-28 found that four of the six name
a mechanism without specifying its failure modes, and that the sixth introduced two
Critical defects. See [`review-2026-09-28.md`](review-2026-09-28.md).

| Was | Mechanism | ADR | Known open |
|---|---|---|---|
| §8.1 Pairing transfer unspecified | Bundle file to B; B's roster ID returns on its ordinary first heartbeat, accepted while A's roster is incomplete and its pairing window is open. **No new wire message type** | [0011](adr/0011-pairing-bundle-file.md) | H-3, H-4, H-5, H-6 |
| §8.2 No single-instance guard | Named mutex `Local\SoloSpeaker`, acquired **before** ledger replay | [0012](adr/0012-single-instance-guard.md) | M-1, M-2 |
| §8.3 `pairKey` in plaintext | DPAPI protects the `pairKey` field only; the rest of `config.json` stays readable | [0013](adr/0013-dpapi-protects-pairkey-only.md) | none - survived review intact |
| §8.4 No tray icon assets | Six icons differentiated by silhouette, colour as reinforcement only | [0014](adr/0014-tray-icons-by-shape.md) | H-8, and no assets exist |
| §8.5 No diagnostics | Rolling local log; **ingress drops aggregated per minute by reason** | [0015](adr/0015-local-rolling-log.md) | M-5 remains open - row 8 bounded its blast radius with the rolling tail, but work item 11 owns the fix: extending the per-minute rollup to presence and ownership transitions |
| §8.6 Asymmetric unmute latency | `bye` field added to wire format v1 | [0016](adr/0016-goodbye-datagram-in-v1.md) | **B-1, B-2 were Critical; both fixed in `design.md` revision 4** |

Issues #1 and #2 - the unreachable version-mismatch producer and the two competing ingress
lists - are also closed, in `design.md` revision 5.

Three of these turned out to be sharper than they first looked, and the reasoning is worth
keeping visible:

**§8.6 was a now-or-never decision, not an optional improvement.** `design.md` §8 freezes
the wire format in phase 1 so that nothing on the wire becomes a two-machine migration. A
`bye` field added later costs exactly the coordinated update that phase re-cut exists to
prevent. Filing it as "nice to have" would have quietly converted it into "never, without
a version bump". The review upheld this framing and rejected the field's *semantics* -
which is a different objection, and a fixable one.

**§8.1 needed no new wire message.** The obvious design - a dedicated pairing datagram -
would itself have had to be frozen into v1. Reusing B's ordinary heartbeat, with a bounded
pairing-mode exception to ingress step 3, avoids adding anything to a format that is about
to be frozen. Authentication is unaffected: the HMAC check still runs first, so enrollment
still requires possession of `pairKey`.

**§8.2's ordering is the substance.** A second instance is not merely redundant. Starting
while the first holds a legitimate mute, it would replay the ledger, restore the endpoint,
and clear the entry - leaving the first instance holding a mute whose recovery record no
longer exists. A hard kill after that strands the endpoint permanently. The guard must
precede ledger replay, which slightly qualifies §7.3's "before anything else".

`design.md` §9.2's risk register is otherwise unchanged. §9.2-3 - whether render-mute
disturbs capture sessions - remains open by decision: it will be verified against the
running app in phase 2 rather than by a standalone spike.
