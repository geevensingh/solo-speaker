# Agent Instructions - solo-speaker

These are the default instructions for any AI coding agent (Copilot CLI, Copilot
coding agent, Cursor, Claude Code, etc.) working in this repository. Follow them
unless a task explicitly overrides a specific rule.

Sections marked **TBD** describe decisions that have not been made yet. The rule
in a TBD section is still binding: it says the decision requires explicit
approval before an agent makes it implicitly by writing code.

## 1. Source of Truth

- **[`docs/design.md`](docs/design.md) is the authoritative product &
  architecture spec.** Read the relevant sections before making non-trivial
  changes.
- **Never silently deviate from the spec** (the arbitration model, the roster,
  the wire format, the writer set, the phase boundaries, the recorded
  decisions in section 9.1). If a request contradicts the spec, or you believe
  a deviation is warranted, give a detailed written explanation of why and ask
  for explicit permission before implementing.
- If a deviation is approved, update `docs/design.md` in the same PR as the
  code change, and bump the revision note if the change is material.
- `docs/design.md` section 9.1 records three decisions (D-1, D-2, D-3) that are
  explicitly reversible. "Reversible" means the user can revisit them; it does
  **not** mean an agent may quietly re-decide one mid-task.

## 2. Tech Stack

Chosen 2026-09-27 with explicit user approval, and recorded in
[`docs/adr/`](docs/adr/). The ADR is the rationale; this section is the rule.

What the design fixes (`docs/design.md` sections 6, 7.1, and 7.2, and
`README.md` -> Platform):

- **Platform:** Windows only.
- **Audio:** Core Audio / WASAPI, for both mute actuation and microphone
  capture-session detection.
- **Transport:** UDP broadcast on the local subnet. No cloud service, no
  account, no broker.
- **Shape:** one tray process per machine.

What is now chosen:

| Concern | Choice | ADR |
|---|---|---|
| Language / runtime | C# on .NET 10 (LTS to Nov 2028) | [0002](docs/adr/0002-csharp-on-dotnet-10.md) |
| Packaging | Self-contained single-file exe + PowerShell install/uninstall | [0003](docs/adr/0003-single-file-exe-packaging.md) |
| Tray UI | WinForms `NotifyIcon` | [0004](docs/adr/0004-winforms-notifyicon-for-tray.md) |
| Win32 / COM interop | CsWin32 source generator | [0005](docs/adr/0005-cswin32-for-wasapi-interop.md) |
| Test runner | xUnit, plus a two-node in-process harness | [0006](docs/adr/0006-xunit-and-two-node-harness.md) |
| Formatter / linter | `dotnet format`, plus PSScriptAnalyzer for `scripts/` | - |
| Build / CI | GitHub Actions on `windows-latest` | - |

Non-negotiable defaults:

- **`TreatWarningsAsErrors` is on repo-wide** (`Directory.Build.props`). Do not
  suppress a warning at the project level to make a build pass; fix it, or
  suppress it at the single call site with a comment saying why.
- **`InvariantGlobalization` is on.** This is not a size optimization. Section
  5.3 of the design requires ordinal byte equality with no casing, culture, or
  normalization semantics anywhere in the roster-comparison path, and invariant
  mode makes a culture-sensitive comparison impossible to introduce by
  accident. Do not turn it off.
- **`SoloSpeaker.Core` targets `net10.0`, not `net10.0-windows`.** This is what
  makes section 3's pure-reducer rule enforceable by the compiler rather than by
  discipline. Do not add a Windows-specific target, reference, or package to
  `SoloSpeaker.Core` - if something there needs a Windows API, the boundary has
  leaked and the fix is on the other side of it.
- **Trimming stays off** for the published executable. The app reaches COM
  interfaces through generated interop; a trimmed build is a class of silent
  runtime failure this project cannot absorb, and the size saving is irrelevant
  at two machines.

The approval rule still applies to everything not listed above:

- **Do not introduce a new framework, package manager, test runner, linter,
  formatter, or third-party dependency without explicit approval.** Picking one
  by writing the first file is not approval; it is the decision being made
  implicitly, which is exactly what this rule exists to prevent. The current
  third-party surface is deliberately small - `Microsoft.Windows.CsWin32`,
  xUnit, and `coverlet.collector` - and additions to it are a decision, not an
  implementation detail.
- When something is added, record it here and in an ADR in the same PR.

## 3. Repository Layout

```
AGENTS.md                      # this file
README.md                      # public pitch / problem statement
SoloSpeaker.slnx               # solution
Directory.Build.props          # shared compiler settings; warnings-as-errors
global.json                    # SDK pin
PSScriptAnalyzerSettings.psd1  # PowerShell lint config
docs/
  design.md                    # authoritative design spec
  implementation-plan.md       # how it gets built, tested, deployed
  manual-test-matrix.md        # per-release checklist for what CI cannot reach
  wire-format.md               # normative v1 datagram format, frozen
  adr/                         # decision records
src/
  SoloSpeaker.Core/            # net10.0 - pure. No Windows reference, by design
    Abstractions/              # the I/O seams
  SoloSpeaker.App/             # net10.0-windows - tray, interop, sockets, disk
    NativeMethods.txt          # CsWin32 surface, annotated per design section
    app.manifest               # asInvoker, PerMonitorV2
tests/
  SoloSpeaker.Core.Tests/      # net10.0 - everything CI can prove
scripts/
  install.ps1  uninstall.ps1  Test-Ascii.ps1
```

Constraints that come from the design and are not open questions:

- `docs/design.md` section 6 names the components: `StateMachine`, `PeerLink`,
  `MicWatcher`, `MuteActuator`, `StateStore`, `Ledger`, `HotkeyListener` +
  `TrayIcon`. New code goes in the component it belongs to; do not invent a
  parallel decomposition.
- **`StateMachine` is a pure reducer** (`(currentState, event, now) ->
  (newState, effects)`) and **all I/O lives at the edges**. This is called out
  in the design as the main testability decision. Do not put a socket, an audio
  device handle, a clock read, or a disk write inside the reducer.
- **The reducer lives in `SoloSpeaker.Core`; everything it talks to is an
  interface in `SoloSpeaker.Core/Abstractions/`.** `IClock`, `IPeerTransport`,
  `IProximitySource`, `IMuteActuator`, `IMicWatcher`, `IStateStore`.
  Implementations live in `SoloSpeaker.App`. A test that needs a real socket or
  a real audio device to exercise arbitration logic means the boundary leaked.
- **Nothing outside the composition root reads the system clock.** No
  `DateTime.UtcNow`, no `Environment.TickCount64` - take `IClock`. Every
  interval in the design (2 s cadence, 10 s presence window, 12 s quarantine,
  5 s debounce, 250 ms self-change suppression) is tested against a controlled
  clock, and one real-clock call makes those tests slow or flaky.

## 4. Coding Conventions

Language-specific conventions land here when the stack is chosen (section 2).
The rules below are language-agnostic and apply now.

### General

- **Never swallow errors.** Log and rethrow, or surface to the user. A silent
  catch in this codebase means a machine is muted and nobody knows why.
- **Validate all external input at the boundary and reject rather than
  coerce.** This covers peer datagrams, persisted state read back from disk,
  the mutation ledger, and device enumeration. The design is explicit about
  this: a datagram missing `micLive` is a version mismatch, not `false`
  (`docs/design.md` section 10). Coercing an absent field to a default is the
  bug class that muted both machines in revision 1.
- **Fail audible, never fail silent.** Any ambiguity - unknown owner, missing
  peer, live local microphone - leaves the machine audible
  (`docs/design.md` section 5.5). When you are unsure which way an error path
  should resolve, it resolves toward audible.

### Naming

- Classes / types: `PascalCase`. Variables / functions: `camelCase`.
  Constants: `UPPER_SNAKE`. File naming convention is set with the stack.
- **First-party environment variables use the `SOLO_SPEAKER_*` prefix.** The
  prefix is runner- and tool-agnostic on purpose, so the variable name does not
  couple to a current implementation choice and does not have to be renamed
  when the toolchain changes.
- **Use descriptive names.** Variables, parameters, and functions must use
  whole-word, intention-revealing names - not single letters or ad-hoc
  abbreviations like `a`, `b`, `x`, `y`, `tmp`, `val`, `data2`. Prefer
  `activeOwner` over `ao`, `nextSeq` over `n`, `timeoutMs` over `t`. **Prefer
  the full word over a shortened form**: write `index` not `idx`, `error` not
  `err`, `request` not `req`, `response` not `res`, `length` not `len`, `count`
  not `cnt`, `previous` not `prev`, `current` not `cur`, `temporary` not `tmp`,
  `value` not `val`. The only generally-accepted abbreviations are
  well-established three-or-more-letter terms (`url`, `id`, `db`, `api`,
  `http`, `json`, `udp`, `rssi`, `hmac`, `lhs`/`rhs`, `min`/`max`), plus the
  spec's own vocabulary (`seq`, `micLive`, `pairKey`) which is normative and
  must not be "improved". Single-letter names are only acceptable in these
  specific idiomatic cases:
  - **Numeric loop counters**: `i`, `j`, `k` in a `for` loop or equivalent
    `while` counter.
  - **Sort comparators**: `sort((a, b) => ...)` - canonical idiom in most
    languages; do not rename.
  - **Destructured domain components**: `let [y, m, d] = ...` and similar where
    the letters map to a well-known mnemonic (year/month/day, x/y/z
    coordinates).
  - **Trivial one-liner identity-style lambdas**: `map(p => p.id)`,
    `filter(b => b.active)`. Acceptable when the lambda body is a single
    property access or comparison and the source collection's element type is
    obvious from context. Anything more complex (multi-line body, multiple
    references to the parameter) must use a real word.

  Anywhere outside these exceptions - including persistent local variables,
  function parameters, generic type parameters that carry meaning, and
  non-trivial callback bodies - use a real word.

### ASCII-only repository

- Tracked files **must be ASCII**, except for the codepoints in the allowlist
  below. Use `-` for em/en-dash, `...` for ellipsis, `->` for right-arrow,
  `<->` for left-right-arrow, `<=` / `!=` / `x` for math, and `[x]` for check
  marks.
- **Allowlist.** These codepoints are permitted because ASCII substitutes would
  materially degrade the documents that use them:

  | Codepoint | Char | Permitted use |
  |---|---|---|
  | `U+00A7` | section sign | Cross-references in docs (`section 5.5` shorthand) |
  | `U+2500` | box horizontal | Component diagrams in `docs/design.md` |
  | `U+2502` | box vertical | Component diagrams |
  | `U+250C` | box down-and-right | Component diagrams |
  | `U+2510` | box down-and-left | Component diagrams |
  | `U+2514` | box up-and-right | Component diagrams |
  | `U+2518` | box up-and-left | Component diagrams |
  | `U+252C` | box down-and-horizontal | Component diagrams |
  | `U+25BC` | down-pointing triangle | Arrowheads in component diagrams |

- Anything outside that table is a violation, including in code, comments,
  commit messages, and new docs. If you genuinely need a new codepoint (e.g. a
  tray UI glyph), add a row here **and** to the lint gate's `ALLOWED` set with a
  comment explaining why - do not disable or loosen the check.
- A repo-wide ASCII lint gate is implemented as
  [`scripts/Test-Ascii.ps1`](scripts/Test-Ascii.ps1) and runs in CI. Its
  `$allowed` set is a transcription of exactly the table above; adding a
  codepoint to one without the other is the drift the check exists to prevent.
  Run it locally with `.\scripts\Test-Ascii.ps1`, or `-Fix` for a table view
  with suggested substitutes.

## 5. Testing

- **Always add/update tests** for logic changes. No test = not done.
- Test names describe behavior: `returns both machines audible when activeOwner
  is outside the roster`.
- `docs/design.md` section 10 is the authoritative test plan. It tracks the risk
  register in section 9 directly. When you add a behavior, add the row; when you
  change a behavior, change the row in the same PR.
- The reducer is pure (section 3), so the **entire arbitration model is
  unit-testable in-process** without sockets, audio devices, or a second
  machine. If a test for arbitration logic needs a socket or an audio device,
  that is a signal the I/O boundary has leaked into the reducer - fix the
  boundary, not the test.
- Run the full lint + test + build sweep before declaring completion
  (section 7).

### Fast inner loop

For incremental work, prefer a fast inner loop over the full Definition of Done
cycle (section 7) on every iteration:

```powershell
dotnet test tests\SoloSpeaker.Core.Tests --filter FullyQualifiedName~QuarantineTests
dotnet build -c Debug
```

- **Run the narrowest thing that covers the change.** Target the specific test
  file or module during iteration; escalate to the full suite only at the end
  or when targeted results are ambiguous.
- **Skip the production build during iteration** if the type/compile gate
  already catches the same errors. Build when you are about to declare a task
  done, or when the change could affect packaging, startup, or
  production-only behavior.
- For sustained work on a single area, kick off long-lived watchers in
  background terminals so each save checks incrementally. Stop the watchers
  before running the full Definition of Done cycle to free up CPU / RAM.
- **Align local with CI before non-trivial work.** CI pins the SDK from
  `global.json` and runs `dotnet restore` against the committed
  `PackageReference` versions. A "passes locally" claim from a different SDK
  feature band is not equivalent to "passes on CI"; check `dotnet --version`
  against `global.json` if a build behaves differently in the two places.

## 6. Security & Privacy

- **`pairKey` is a secret.** Per `docs/design.md` sections 7.1, 7.5, and 7.7 it
  is a 256-bit value that keys the datagram HMAC, lives only in local
  `config.json` on the two machines, never crosses the network, and is
  established by a hand-copy pairing ceremony.
  **Never** commit it, log it, echo it to the tray or console, include it in a
  crash dump or telemetry, or transmit it in cleartext. Revision 1's Critical
  defect was broadcasting the keying secret; do not reintroduce it in a new
  shape (debug logging counts).
- **Never log or transmit audio content, capture-session payloads, or PII**
  beyond what the feature requires. "Which process holds a capture session" is
  device metadata the design needs; the audio itself never leaves the process.
- **No secrets in source.** Secrets live in per-machine local state that is
  gitignored, never in tracked files.
- Enforce the limits and bounds the spec defines (the `seq` delta bound, the
  presence window, the roster size of two) at the point where untrusted data
  enters, not only where it is consumed.
- Sanitize any user-provided or device-provided string before rendering it in
  the tray or any UI surface. Device friendly-names are attacker-influenced in
  the general case.

## 7. Definition of Done

These checks run before declaring a task done, **not** on every save. For
inner-loop iteration use the fast loop (section 5); the steps below are the
final sweep.

The full sweep, from the repository root:

```powershell
dotnet format --verify-no-changes
dotnet build -c Release
dotnet test -c Release --no-build
dotnet publish src\SoloSpeaker.App\SoloSpeaker.App.csproj -c Release -o artifacts\publish
.\scripts\Test-Ascii.ps1
Invoke-ScriptAnalyzer -Path .\scripts -Recurse -Settings .\PSScriptAnalyzerSettings.psd1
```

`dotnet publish` is in the sweep rather than only at release. Deployment is a
single hand-copied executable (section 2), so a packaging regression is
otherwise invisible until the day it matters.

Before finishing a task:

1. **Lint passes.** All configured static gates (type/compile check, format
   check, ASCII check, any pattern checks) are green.
2. **Tests pass.** The full unit suite, plus any integration suite the change
   touches.
3. **Build succeeds** in the production/release configuration.
4. **ASCII check passes** - no new non-ASCII codepoints outside the allowlist
   (section 4).
5. **Formatter has been run** over changed files, or format-on-save is
   confirmed active.
6. **Only run the suites that exist - do not introduce new toolchains to
   satisfy this checklist.** If a suite is not set up yet and the task is
   scaffolding, set it up per the spec and per section 2's approval rule. If a
   suite is not set up and the task is not scaffolding, say so explicitly in
   the completion message rather than silently skipping the step or silently
   inventing the tooling.
7. **No new compiler errors or warnings introduced.**
8. **Spec is updated** if behavior or architecture changed
   (`docs/design.md`, section 1).
9. **`README.md` updated when user-facing behavior changes.** `README.md` is
   the public-facing explanation of what the tool does and how it decides.
   When a change adds, removes, or significantly alters user-visible behavior -
   including the Status table's phase states - update it in the same PR.
   Internal refactoring and bug fixes typically do not require an update.
10. **Never work around a dependency resolver's conflict errors with force or
    override flags** (for example npm's `--legacy-peer-deps` / `--force`, or
    the equivalent in whatever package manager is chosen) **without explicit
    user approval.** These flags are lockfile-drift attractors: they skip
    recording resolution detail that a clean CI install later rejects, breaking
    CI for everyone. If the resolver refuses an install, stop and ask whether
    to bump the conflicting package, pin a different version, or accept the
    override - do not work around it silently.

    If the user does approve an override, it must be persisted before commit
    by either committing the matching config so subsequent clean installs use
    the same resolution, or regenerating the lockfile afterward with the
    override removed so the committed lockfile is valid under default
    settings. Either way, a clean lockfile-exact install must succeed before
    commit.

## 8. Git & PR Hygiene

- Small, focused commits with imperative subject lines (e.g., `Add seq delta
  bound to PeerLink datagram validation`).
- Stage files explicitly by path. **Never** run `git add -A`, `git add .`, or
  `git add --all`. This prevents committing unrelated edits, generated files,
  or session-state artifacts.
- **Never** run `git rebase` or `git pull --rebase`. When branches diverge, use
  `git pull --no-rebase` (merge), or stop and ask. The "do not rewrite or
  force-push" rule below already prohibits the destructive form; this rule
  prohibits the local form too.
- Never commit secrets, `.env`, dependency directories, build output, local
  machine state (`pairKey`, the roster, the mutation ledger), or editor files
  beyond what `.gitignore` already covers.
- Do not rewrite or force-push shared branches.
- **NEVER bypass branch protection.** Do not use `gh pr merge --admin`,
  `--force`, or any other flag/UI/API path that bypasses required reviews,
  required status checks, or unresolved review-thread blocks. This rule has
  zero exceptions, including:
  - When the user owns the repo and `--admin` would technically work.
  - When the change "looks trivial" (a comment-only edit, a typo fix).
  - When CI is "obviously" green and the only block is an unresolved review
    thread or missing approver.
  - When the user is unavailable and the PR has been sitting.
  - When you have local user approval to merge - that is not the same as
    approval to **bypass policy**. The policy exists for the human workflow
    gates (review, conversation resolution, required checks); user-of-the-
    moment consent does not waive those gates.

  If a PR is blocked, surface the block in plain language (which gate is
  failing, e.g., "1 unresolved review thread", "review required", "1 of 10
  checks pending"), explain how it can be cleared (resolve thread, request
  review, wait for CI), and stop. Do not propose `--admin` as an option in
  `ask_user`. The user can clear the block themselves through the GitHub UI;
  agents do not bypass it. (Resolving review threads the agent has legitimately
  addressed with a pushed commit is a different action - see "Responding to PR
  review feedback" below.)

  **`main` is not currently protected on this repo.** That is a repo-settings
  gap, not a licence. The rule above is the standing policy for when protection
  is enabled, and the underlying workflow gates (human review, green CI,
  resolved threads) apply to agent behavior regardless of whether the platform
  is enforcing them today. An agent must not merge its own PR on the grounds
  that nothing is technically stopping it.
- **Every AI-assisted commit carries two distinct kinds of trailer**: one for
  *attribution* (who wrote it) and one for *session identity* (which agent
  session produced it). They answer different questions. Do not conflate them,
  and do not drop one because the other is present.

  ```
  Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>
  AI-Local: <local-session-id>
  AI-Cloud: <cloud-session-id>
  ```

  - **`Co-authored-by:`** is attribution. The value above is what the Copilot
    CLI runtime emits. If a runtime you are using mandates a different display
    name against this same account ID, follow the runtime and flag the mismatch
    so this line can be updated - do not leave the two silently divergent.
  - **`AI-Local:` / `AI-Cloud:`** identify the session, so the user can resume
    it to address review feedback. The `AI-` prefix marks these as agent-runtime
    identifiers so they are greppable and unambiguous. Trailer form is
    deliberate: the IDs stay machine-readable via
    `git log --format='%(trailers:key=AI-Local)'` and can be appended with
    `git interpret-trailers`.
  - Canonical sources for the Copilot CLI runtime are the same ones the PR
    Session block uses: `workspace.yaml` -> `id` for `AI-Local`, and
    `workspace.yaml` -> `mc_session_id` for `AI-Cloud`. `workspace.yaml` lives
    at the root of the agent's session folder (typically
    `~/.copilot/session-state/<local-session-id>/`).
  - **Emit `AI-Cloud` only when a cloud session exists and its ID differs from
    `AI-Local`.** A local-only CLI session has no `mc_session_id` at all; that
    is the normal shape for local work, not a corrupted workspace, so the line
    is simply omitted and needs no explanatory note.
  - **Never invent values.** If `workspace.yaml` is genuinely unreadable, omit
    the session trailers and say so in one line in the commit body.
  - Put all trailers in a single block at the end of the message, one per line,
    with no blank lines between them.

  This is the git-native record of which session produced a commit. It does not
  replace the PR-description Session block below, which is the GitHub-surface
  record; nor does the PR block replace these trailers. Because the trailers
  live in the commit itself, session traceability survives regardless of which
  merge strategy is used.

- When opening or triaging an issue, apply exactly one priority label:
  `priority:high`, `priority:medium`, or `priority:low`. These three labels are
  the only priority signal in the repo (we do not use a Project board field).
  Existing kind/area labels (e.g., `bug`, `documentation`, `enhancement`,
  `accessibility`) are orthogonal and still apply. The three `priority:*` labels
  do not exist in this repo yet and must be created before this rule can be
  followed; flag it rather than silently skipping the label.

### PR descriptions for agent-authored PRs

When the **Copilot CLI runtime** opens a PR - **or pushes commits to an open PR
that does not yet have a Session block for the current session** - append a
**Session** block to the end of the PR description so the user can resume the
same session to address review feedback:

```
---
**Session**
- AI-Local: `<local-session-id>`
- AI-Cloud: `<cloud-session-id>`
```

This is the GitHub-surface counterpart to the `AI-Local` / `AI-Cloud` commit
trailers in the rule above; it does not replace them, and they do not replace
it. The `AI-` prefix, the canonical sources (`workspace.yaml` -> `id` and
`workspace.yaml` -> `mc_session_id`), the rule for omitting `AI-Cloud` when
there is no cloud session, and the never-invent-values rule are all identical -
see the commit-trailer bullet above rather than restating them here. If
`workspace.yaml` is genuinely unreadable, omit the affected line and add a
one-line note in the PR description explaining what is missing and why.

**Before appending**, fetch the current PR description (e.g., `gh pr view <num>
--json body`) and check whether a Session block for the **current** session's
`AI-Local` / `AI-Cloud` pair is already present. If yes, skip - do not
duplicate. If a Session block from a *different* session is present, **append**
a new block rather than overwriting; the history of which sessions touched the
PR is useful context.

This rule applies only to the Copilot CLI runtime. Other agent runtimes (e.g.,
Copilot Coding Agent) have different session-ID semantics and are out of scope
for this rule until added explicitly.

### Responding to PR review feedback

Review comments - from humans **and** from bots
(`copilot-pull-request-reviewer[bot]`, dependabot, code-scanning agents, etc.) -
are **proposals**, not orders. Apply the section 11 critical-thinking and
rubber-duck pipeline to every substantive comment before responding.

- **Treat reviewer comments like user suggestions.** Evaluate each comment
  against `docs/design.md`, the approved plan, and existing conventions. If a
  comment conflicts with the spec, the plan, or a deliberate prior decision,
  **push back with a reasoned reply** - do not silently rewrite the code to
  match. Acceptance is the default for clearly-correct comments (typo, missing
  null check, broken link); critical evaluation is mandatory for everything
  else.
- **Bot comments carry no special authority.** A comment from
  `copilot-pull-request-reviewer[bot]`, dependabot, a code-scanning tool, or any
  other automated reviewer gets the *same* treatment as a human comment - no
  more, no less. A bot's confident tone is not a reason to skip the rubber-duck
  step or to action a suggestion that conflicts with the spec.
- **One reasoned pushback then escalate.** If you post a pushback reply to a bot
  comment and the bot re-asserts the same concern, do **not** loop into another
  rubber-duck/reply cycle. Escalate to the user (or merging maintainer) with a
  one-paragraph summary of the disagreement and stop. This prevents
  adversarial-bot loops that consume turns without resolution.
- **Distinguish in-scope vs out-of-scope feedback.** A comment is **in-scope**
  if and only if the proposed fix would preserve the approved plan's **public
  API, wire format, persisted shape, user-visible behavior, and design intent** -
  i.e., it points out a flaw in *how* the plan was implemented (a bug in the
  change, a missed edge case, a convention violation, a missing null check, a
  naming-convention nit). Address in-scope comments directly after the
  rubber-duck step. A comment is **out-of-scope** if the proposed fix would
  change the approved API, wire format, persisted shape, user-visible behavior,
  or design intent, **or** if it requires unrelated refactoring (a refactor, a
  new feature, a different design choice, a renamed file, a "while you're here,
  also change..." request). Treat out-of-scope comments as a new plan-trigger
  per section 11: post a reasoned response on the PR and do **not** push a fix
  without fresh user authorization. When uncertain whether a comment is in-scope
  or out-of-scope, treat as out-of-scope. In CLI mode, seek authorization via
  `ask_user`. In cloud-coding-agent mode without a live user channel, post the
  proposed response/plan as a PR comment and **stop** until the user or merging
  maintainer explicitly authorizes proceeding - silence is not authorization.
- **Rubber-duck before responding to substantive comments.** Substantive
  comments (any non-trivial code change) require the section 11 rubber-duck
  pipeline before you respond. You may batch related substantive comments and
  rubber-duck them together (e.g., five comments about the same function get one
  rubber-duck pass, not five). **Trivial comments may be actioned directly
  without rubber-ducking**, where "trivial" is narrowly scoped to: an obvious
  typo or grammar fix in a code comment or documentation; a broken-link fix; a
  lint nit produced by an automated tool (e.g., naming-convention rename,
  missing semicolon, import order). When uncertain whether a comment is trivial,
  treat as substantive and rubber-duck.
- **Resolve threads only AFTER pushing the addressing commit, and verify the fix
  landed.** Never resolve a thread before the addressing commit is pushed. After
  pushing, **always** resolve the thread - reviewer bots do not return to
  verify, so leaving addressed threads open is noise that hides genuine
  unresolved items. For pushback-only responses (no code change), resolve only
  after the user or merging maintainer has accepted the reply; do not
  unilaterally resolve a thread on a comment you disagreed with. Before
  resolving, re-read the thread and confirm the pushed change (or accepted
  reply) actually addresses what was raised.

  **Resolving an addressed thread is not bypassing a branch-protection block.**
  The existing rule above ("The user can clear the block themselves through the
  GitHub UI; agents do not") prohibits unilateral bypass via `--admin` /
  `--force` - it does not prohibit resolving a thread the agent has legitimately
  addressed with a pushed commit. The two actions are distinct: bypass overrides
  a policy gate; thread resolution signals that the work the reviewer requested
  is complete.

- **Never bypass branch protection to "clear" unresolved threads.** Restated
  from the rule above: if a PR is blocked by *unresolved* threads (i.e., threads
  where you have not pushed an addressing commit or do not have accepted
  pushback), the path forward is to resolve them with fixes or accepted pushback
  replies - never `gh pr merge --admin`, `--force`, or any other bypass. Zero
  exceptions, including bot threads on trivial-looking comments.

### Auto-merge

Auto-merge (`gh pr merge --auto`) is the policy-compliant way to indicate "this
PR should land once gates pass". It is **not** a bypass: GitHub holds the merge
until all required gates (reviews, conversation resolution, required status
checks) pass, then merges automatically. (If all gates are already satisfied
when you enable auto-merge, GitHub will merge **immediately** - so only enable
auto-merge when you actually want the merge to proceed.) Compare to `gh pr merge
--admin`, which IS a bypass and is prohibited under all circumstances (see the
branch-protection rule above).

**Default: OFF.** The agent does not enable auto-merge on its own judgment. The
user merges manually, or explicitly authorizes auto-merge per PR.

**Enable auto-merge only when all of the following are true:**

- The user has **explicitly authorized merging this PR** with an unambiguous
  verb-led phrase: "ship it", "auto-merge it", "approve and merge", "merge when
  green", or equivalent. Per **section 10 "Discussion is not approval"**,
  ambiguous conversational signals ("looks good", "go ahead", "yes") do **not**
  count. If you are unsure whether a phrase qualifies, ask via `ask_user`; do
  not enable auto-merge on a guess.
- The authorization applies to the **current state** of the PR. If the user's
  authorization is combined with new requested scope ("ship it after adding X"),
  complete the new scope first, rerun the section 7 DoD checks, rubber-duck the
  final diff, then seek **fresh authorization** for the new final state.
- All intended commits for this PR have been pushed. Do not enable auto-merge
  while still iterating on the changeset.
- The PR is not a draft.
- The section 7 Definition-of-Done checks pass locally.
- You have rubber-ducked the **final state** of the PR - not just the plan (the
  final state may differ if rubber-ducked review feedback changed the diff).

If any criterion is unmet, do not enable auto-merge. Surface what is missing in
plain language and wait for the user, exactly as you would for a
branch-protection block.

**Prefer the squash strategy:**

`gh pr merge <number> --auto --squash`

This repo currently permits merge commits and rebase merges as well; squash is
the convention, not a platform constraint. Do not pick a different strategy
without asking.

**Proactive recommendation for low-risk classes.** For PRs in the following
narrowly-defined low-risk classes, the agent **should** recommend auto-merge in
the PR description (e.g., a one-line "Recommend auto-merge once CI passes -
reply 'ship it' to enable.") so the user does not have to ask:

- **Docs-only**: changes to `*.md`, `docs/**`, or comments inside config files.
  No executable or runtime behavior touched (no logic, no schemas, no build
  steps, no tests).
- **Lint/format-only**: changes produced by an automated formatter or linter
  with no semantic diff.
- **Patch-level dev-dependency bumps** from dependabot or equivalent, where the
  lockfile is the only meaningful change, the dependency is a development-only
  dependency (not a runtime one), and CI passes.
- **Typo fixes in code comments or docs only** - not in user-visible strings.

**Anti-lawyering rule:** if any touched file or hunk falls outside its low-risk
class, the **whole PR** is outside the class. A "docs-only" PR that also touches
a source file is not docs-only; do not proactively recommend auto-merge.

For any PR outside these classes - new features, refactors, behavior changes,
wire-format or persisted-shape changes, infra, test additions/removals, anything
that changes a public API - the agent should **not** recommend auto-merge
proactively. Let the user decide unprompted.

**After enabling auto-merge**, keep it enabled only for **purely mechanical**
follow-up commits:

- Lint or format touch-ups produced by an automated tool.
- Comment-only edits (changes to code comments or doc files).
- The literal text change a reviewer requested for a trivial issue (typo or
  broken link in a code comment or doc file only), with no surrounding logic
  touched.

For any non-mechanical follow-up (additional code, scope expansion, refactor,
new tests with new assertions, behavior change), **disable auto-merge first**
(`gh pr merge <num> --disable-auto`), push the commit, then either seek fresh
merge authorization or leave auto-merge off and let the user re-enable. This
prevents the case where auto-merge silently merges code the user never approved.

**Recovery if you forget to disable.** If you push a substantive follow-up
commit while auto-merge is still enabled:

1. Immediately run `gh pr merge <num> --disable-auto`.
2. Disclose the mistake in plain language to the user (in the next response or
   as a PR comment) - name the commit and why it should have triggered a
   disable.
3. Seek fresh merge authorization before re-enabling auto-merge.

If the PR has already merged before you can disable, do **not** silently move
on. Report the merge to the user with the same disclosure (which commit, why it
was substantive, what gates ran) and ask whether to revert, follow-up-fix, or
accept.

## 9. Scope Discipline

- **Scope discipline governs plan execution, not option weighing.** Once a plan
  is approved, stick to it: make surgical changes that fully address the
  request, and do not refactor unrelated code, rename files, or reformat
  untouched areas. This rule does **not** apply to the option-weighing /
  recommendation phase - see section 11 "Don't default to the minimal change".
- **Plans must scope to the user's stated request plus tightly-coupled work.** A
  plan can legitimately include a larger, cleaner refactor when the refactor is
  on the path of the request or genuinely tightly coupled to it. Refactors of
  code **not** on that path require the user to explicitly request or accept the
  refactor as scope - not just rubber-stamp a plan that happens to include it.
  The user's described scope is the **outer** bound on what the agent
  recommends; "architecturally correct" is not a license to inflate beyond
  architectural necessity.
- If you find a tightly-coupled bug caused by the code you're changing, fix it.
  Otherwise, file an issue and move on.
- Prefer ecosystem tooling (scaffolding generators, codemods, package-manager
  commands) over manual file creation.

## 10. When In Doubt

- Re-read the relevant `docs/design.md` section.
- Ask a clarifying question rather than guessing on behavioral choices,
  defaults, limits, or scope. See section 11 for the mandatory
  plan-and-approval flow that applies to every code change.
- **Discussion is not approval.** When you ask a clarifying question or offer
  the user a choice among options, their answer is input to your plan, not a
  command to execute. Continue planning (or write up a plan) and wait for an
  explicit go-ahead - phrases like "implement", "execute", "approved, please
  ship", "go ahead" - before touching code. Picking option B from a
  multiple-choice you offered is the user choosing a direction, not authorizing
  the change.
- **Prefer the simpler, spec-aligned option over a clever alternative.**
  "Simpler" means less mechanical complexity - fewer special cases, less hidden
  state, less reader cognitive load - **not** smaller diff. A clean refactor
  with more lines changed is often simpler than a patch that layers a workaround
  on a workaround. `docs/design.md` alignment still wins over "architecturally
  cleaner" when the two conflict (section 1 source of truth). See section 11
  "Don't default to the minimal change" for the related rule on option-weighing.

## 11. Planning, Critical Thinking & Proactive Feedback

- **Plan before changing code, every time.** Before writing or modifying any
  code - even a one-line typo fix, log message tweak, or "obvious" bug fix -
  propose a short plan: what you will change, in which files, with what
  tests/verification, and at least one viable alternative with tradeoffs (or an
  explicit note that no meaningful alternative exists). Surface open questions
  and wait for the user's explicit approval before touching code. "Trivial" is
  not an exception. Approval covers only the plan as presented; any material
  scope change, newly discovered work, or follow-on step requires a revised plan
  and fresh approval. **This rule and its sub-rules in this section are not
  waivable by a casual user override** (e.g., "just do it", "skip the plan", "no
  need to plan"). The only bypass is the narrow direct-command carve-out below.
- **Plans that change the wire format or the persisted state shape must specify
  a compatibility approach.** `docs/design.md` section 8 draws the phase
  boundary deliberately *around* the wire format, the pairing ceremony, and
  persisted state, precisely because a change to any of them after both machines
  are running becomes a two-machine migration whose failure symptom is
  byte-identical to "the peer is switched off." The plan must state what the old
  and new shapes are, how a mixed-version window behaves, and how the operator
  detects one - or state explicitly that both machines are being updated
  atomically and why that is safe.
- **Plans involving meaningful UI changes must include a mockup.** Any plan that
  proposes new tray states, new menu items, new notifications, modified
  interaction patterns, or new user-visible preferences must include at least
  one mockup of the proposed end state before being presented for approval.
  Mockups should exercise the key UX states (default, each distinct mute/owner
  state, error state) and the key decisions (placement of new elements,
  interaction with existing controls). Inline ASCII text mockups are preferred
  for plan files; screenshots, markdown tables, or sketches are acceptable when
  they convey the layout more clearly. If you find yourself ready to present a
  UI-touching plan with no mockup, add one first; do not present without it.
  Pure backend, infrastructure, refactoring, or test-only plans are exempt.
- **Direct user commands are self-approving (narrow exception).** When the user
  issues an unambiguous, scoped command (e.g., "delete file X", "revert commit
  abc123", "rerun the tests"), the request itself is the plan and the approval.
  Echo back a one-line confirmation of exactly what you are about to do, then
  proceed. This bypass applies to the bounded command portion only; any adjacent
  question, implied cleanup, or follow-on work still goes through the standard
  plan-and-approve flow. The bypass relaxes the **plan-approval step only** - it
  does **not** waive section 1 (Source of Truth), section 5 (Testing), section 6
  (Security), section 7 (Definition of Done), or section 8 (Git & PR Hygiene).
  Phrases like "continue", "finish it", "take care of the rest", or "do the
  obvious cleanup" are **not** unambiguous commands and do **not** trigger this
  bypass. If a command's scope, blast radius, or side effects are unclear, fall
  back to the normal plan-and-approve flow.
- **Bug reports and feature ideas are plan-triggers, not execute-triggers.** A
  user message that describes a problem ("X is broken", "the spacing is off",
  "I noticed Y", "this seems wrong", "this looks weird") or proposes an idea
  ("we should add X", "could we have Y do Z", "what if we...") is a request to
  investigate and propose a plan - it is **not** authorization to edit, test, or
  commit. The execute step requires an unambiguous command verb directed at you
  in the same or a later turn ("fix it", "go", "implement that", "commit it",
  "ship it", "execute"). When in doubt, the default is plan-and-ask. The narrow
  direct-command carve-out above applies only to bounded imperative requests
  ("revert abc123", "run the tests", "delete file X"); a bug report does not
  qualify even when the user's intent to eventually fix it is obvious. The user
  reporting a problem is delegating diagnosis and planning to you, not
  authorization.
- **CI failures are plan-triggers, not retry-triggers.** Never re-run a failed
  CI job, "Re-run all jobs", or push a speculative fix on the agent's own
  judgment when CI is red, even when the changeset on the run looks obviously
  unrelated to the failing test. **On `main`, this is zero-tolerance**: never
  autonomously retry a red CI run, full stop. On PR and feature branches the
  same no-autoretry rule applies, but you may at least propose a retry as part
  of a plan - still subject to user authorization before acting. The default
  response to any CI failure is: tell the user the job failed, summarize what
  broke (which job, which test or step, the assertion or exit code, what the
  changeset on the run actually touched), and ask what to do. When the failure
  looks like a flake, propose filing a `flaky-test` issue so it can be hardened
  rather than re-running silently - silent retries hide both genuine flakes (so
  they never get fixed) and genuine regressions (so they ship anyway, with a
  green second attempt covering for the red first attempt). The direct-command
  carve-out applies only when the user has issued an explicit retry instruction
  in the same or a recent turn (e.g., "re-run that job", "rerun the failed
  attempt"); the agent's own judgment that "this is probably a flake" is not
  such a command. This rule is distinct from the fix-and-push rule for
  agent-caused breakage: when the agent's own change broke CI on `main`, the
  agent must still push the fix and watch the re-run go green before declaring
  the task done. That rule is about *fixing* known breakage caused by the agent;
  this one is about not *retrying* unknown failures on a hunch.
- **Rubber-duck every plan before presenting it - unconditionally.** Every plan,
  every time, regardless of size, medium, or perceived triviality. Mechanics:

  - **Verified critic-capable runtimes.** Copilot CLI (`task` tool), Claude Code
    (`Task` tool). If a runtime lacks sub-agent invocation, stop and tell the
    user; do not present plans from that runtime.

  - **Critic selection.** The required configuration is the **three-agent
    panel**: `deep-review:skeptic` (attack mindset) + `deep-review:advocate`
    (defense mindset) + `deep-review:architect` (direction mindset), invoked in
    parallel on every plan presented to the user. **The default is panel-only**;
    solo, paired, or otherwise reduced panels are not permitted as a default. A
    reduced panel is allowed only as the explicit failure-mode exception
    documented in "Panelist failure is not a fallback" below, and that exception
    requires user authorization in the same turn. Token and wall-clock cost are
    explicitly accepted as the price of catching the failure modes a solo critic
    misses (over-adoption of one perspective, missed direction concerns, missed
    defense rebuttals). `general-purpose` is permitted to substitute for **at
    most one** missing `deep-review:*` agent in a runtime that lacks it, and its
    prompt must be explicitly adversarial and explicitly take the missing role.
    If two or more `deep-review:*` agents are unavailable, do not present the
    plan; surface the situation to the user and require explicit authorization
    for the substitute configuration (the same "reduced panel" exception above).
    **`explore` is not a critic** - it is a research agent; do not use it for
    rubber-duck. **`deep-review:advocate` remains forbidden as a solo critic**:
    its role is to defend, so a solo-advocate gate is not adversarial.

  - **Critic prompt requirements.** The prompt must (a) include explicit
    adversarial framing ("find at least one weakness; if you genuinely cannot,
    say so explicitly"), and (b) be quoted (or summarized in one line if long)
    in the plan alongside the findings so its quality is auditable. (c) For plans
    that present two or more architectural options, the prompt must explicitly
    include the principle that "smaller diff is not by itself a recommendation
    argument" and that any scope-separation finding must rest on a substantive
    architectural reason. This prevents the critic from generating bare "do not
    fold X" verdicts that the calling agent would then have to discount during
    option weighing (see "Don't default to the minimal change" below).

  - **Panelist failure is not a fallback.** If any panelist's invocation times
    out, errors, or is refused, retry that panelist **once** with a fresh
    invocation. If the retry also fails, surface the failure to the user with the
    panelist role, the failure mode, and what the other panelists found, and
    **require explicit user authorization** to present the plan with a reduced
    panel. Do not silently degrade to a two-agent or solo gate. Self-critique
    inline with plan generation, no matter how rigorous, does not satisfy this
    rule under any circumstance.

  - **Quote findings.** Each panelist's **full transcript** must be preserved
    verbatim in `plan.critic-<role>.md` (where `<role>` is `skeptic`,
    `advocate`, or `architect`) alongside `plan.md`; this is the per-panelist
    record and must not be paraphrased, elided, or truncated. In the inline
    `Pre-presentation gate` section, each *distinct* finding is listed once
    under the panelist who raised it most directly, **verbatim from that
    panelist's output** (the selected quote must not be paraphrased, elided, or
    truncated). If another panelist raised the same concern, note it in the
    cross-critic synthesis (Agreements) by panelist name rather than re-listing
    the finding text. This preserves the full per-panelist record in the
    transcript files without ballooning the inline gate with duplicate text.

  - **Tag each finding's disposition**: `ADOPT` (link to the plan section that
    was changed), `SET ASIDE` (one-line reason), or `OUT OF SCOPE` (one-line
    reason). **For findings tagged `SET ASIDE` or `OUT OF SCOPE` that propose
    scope separation** (split into multiple PRs, defer to a follow-up issue,
    exclude from the current change), the one-line reason must reference a
    substantive architectural property (module boundary, wire format, persisted
    shape, dependency direction, release cadence, code-owner separation), not a
    tactical property (diff size, churn, "smaller PR is easier to review"). This
    mirrors the "Don't default to the minimal change" rule applied to critic
    findings.

  - **Critic-vs-user-preference conflict.** When a finding materially
    contradicts a user choice already made, do not classify it as `SET ASIDE` on
    the strength of the user choice alone. Surface the disagreement via
    `ask_user` with a one-line summary of the critic's reasoning. The user
    re-confirming their choice converts the finding to `ADOPT-via-user-override`,
    not `SET ASIDE`.

  - **Surface findings in the response.** The message that presents the plan
    must either (a) list every finding with its disposition tag, or (b) give
    counts ("N findings, M adopted, K set aside, L out of scope - see gate
    section") so the user can spot suspiciously-zero counts at a glance.
    Material findings (those that change risk, scope, test strategy, or
    recommended approach) must be highlighted by name regardless of which
    presentation mode you pick.

  - **Plan updates.** Any revision that changes scope, approach, test strategy,
    or risk requires a fresh rubber-duck invocation. Append a **new**
    `Pre-presentation gate` section (do not overwrite the prior one) so the audit
    trail is preserved. Typo / formatting edits to the plan itself are exempt.

  - **Anti-bundling.** Do **not** bundle unrelated changes into a single plan to
    amortize the cost of the rubber-duck gate. If two changes do not share a
    causal coupling (one's implementation choice constrains the other, or one is
    a necessary precondition for the other), they are separate plans with
    separate gates. Bundling unrelated changes hides findings and dilutes
    panelist attention. Causally-coupled changes can be planned together but
    should still ship as separate PRs unless the same file is touched by both.

  - **Borderline direct-command requests.** If you find yourself classifying a
    borderline request as a direct command to skip the rubber-duck gate, default
    to plan-and-rubber-duck. The cost of an extra critic invocation is lower than
    the cost of a missed flaw.

  - **Recursive plans.** This rule applies to plans presented to the **user** for
    approval. Sub-agent execution plans within an already-approved parent plan do
    not require their own rubber-duck unless they discover new scope, new files
    outside the parent plan, or changes to the approved approach.

  - **Independence is mechanical, not epistemic.** A sub-agent is the same model
    with separate context; that is enough to catch "I missed this because I was
    attached to my own plan", which is the failure this rule fixes. The
    three-agent panel broadens epistemic coverage further by having three
    distinct role framings (attack / defend / direct) examine the plan in
    parallel - the asymmetry between the framings is what makes the panel
    materially stronger than a solo critic, not the raw count of sub-agents.

  This step applies to plans you author; it does **not** apply to direct-command
  echoes, which are not plans.

- **Every plan ends with a "Pre-presentation gate" section,** regardless of
  medium (inline chat, `plan.md`, `exit_plan_mode`). An empty gate section means
  the plan is not ready to present. Required structure:

  ```
  ## Pre-presentation gate

  ### Skeptic
  - **Invocation**: <one-line summary>
  - **Prompt**: <one-line summary, or "see plan.critic-skeptic.md">
  - **Conclusion**: <one-line, e.g. "12 findings raised" or
    "no material weaknesses found">
  - **Findings**: (one bullet per distinct finding; finding text
    quoted verbatim from this panelist's output, followed by
    disposition + one-line reason)
    - "<verbatim finding text>" -- <ADOPT | SET ASIDE | OUT OF SCOPE> -- <one-line>

  ### Advocate
  - **Invocation**: <one-line summary>
  - **Prompt**: <one-line summary, or "see plan.critic-advocate.md">
  - **Conclusion**: <one-line>
  - **Findings**: (one bullet per distinct finding; finding text
    quoted verbatim from this panelist's output, followed by
    disposition + one-line reason)
    - "<verbatim finding text>" -- <ADOPT | SET ASIDE | OUT OF SCOPE> -- <one-line>

  ### Architect
  - **Invocation**: <one-line summary>
  - **Prompt**: <one-line summary, or "see plan.critic-architect.md">
  - **Conclusion**: <one-line>
  - **Findings**: (one bullet per distinct finding; finding text
    quoted verbatim from this panelist's output, followed by
    disposition + one-line reason)
    - "<verbatim finding text>" -- <ADOPT | SET ASIDE | OUT OF SCOPE> -- <one-line>

  ### Cross-critic synthesis
  - **Agreements**: <findings raised by 2+ panelists, listed once
    here with references to which panelists raised each>
  - **Tensions**: <findings where panelists disagreed, with how
    the tension was resolved>
  - **Total distinct findings**: <N adopted, M set aside, K out
    of scope>
  ```

  For inline plans, render the gate as a fenced block at the end of the message.
  For `plan.md`, render it as the file's last section (or append a new gate on
  plan revision, preserving prior gates). For `exit_plan_mode` content, render
  it as a trailing section before exiting. The `Conclusion` line is required for
  each panelist even when that panelist raised no material findings - it
  disambiguates "panelist was silent" from "panelist spoke and I adopted
  everything".
- **User unavailability is never authorization to proceed.** If the runtime
  reports the user as away, busy, or unresponsive, that does not let you act on
  a guess. Ask the question anyway and wait. Do not assume answers, do not build
  a plan on assumptions, do not proceed autonomously.
- **Use `ask_user` to ask the user; never "make a best guess on autopilot".**
  When you need user input on scope, behavior, defaults, limits, or design, use
  the runtime's `ask_user` tool (or its equivalent) - do not phrase questions as
  plain prose the user might miss. When the set of plausible answers is
  discrete, pass them via the tool's `choices` array. This rule applies in
  **every** runtime mode - interactive, autopilot, fleet, and background - and
  regardless of whether the runtime reports the user as available, busy, or
  away. "Making a best guess on autopilot" is forbidden: an autonomous runtime
  is **not** authorization to guess. If the user is unavailable, ask anyway,
  then stop; do not start implementing. To stop cleanly, use the runtime's
  plan-approval / completion channel (e.g., `exit_plan_mode` in plan mode, or a
  task-completion tool with an ambiguity summary outside it), not free-form
  prose.
- Treat user suggestions as proposals, not orders. Think critically about each
  one before acting.
- When the user proposes an approach, evaluate whether it is sound, complete,
  and consistent with the codebase, the spec, and prior decisions. If you spot a
  flaw, a missed case, a simpler alternative, a better-fitting pattern, or a
  risk the user may not have weighed, **say so before implementing**.
- Offer suggestions and alternatives proactively, not only when asked. Examples
  of things worth raising unprompted:
  - Edge cases or failure modes the proposal does not handle.
  - Cheaper or simpler approaches that achieve the same goal.
  - Conflicts with `docs/design.md`, `AGENTS.md`, or existing conventions.
  - Hidden costs (perf, accessibility, test surface, blast radius, two-machine
    migration burden).
  - Naming, API shape, or signature improvements.
  - Scope concerns: things being included that should be split out, or things
    being omitted that should be folded in.
- Be direct and specific. Vague hedging ("might want to consider...") is less
  useful than a concrete recommendation with a reason. Say what you think the
  right call is, and why.
- Disagree when you have a reason to. Do not silently comply with a request that
  you believe is wrong, incomplete, or risky - raise the concern, explain it,
  and let the user decide. After the user decides, follow their decision unless
  they ask you to push back again.
- **Don't default to the minimal change.** Recommending the smaller of two
  competing options because it has less churn is risk-aversion masquerading as
  pragmatism. Judge options on long-term architectural fit, not diff size.
  **This rule is bidirectional:** it forbids defaulting to the smaller option on
  diff-size grounds, but equally forbids inflating scope beyond architectural
  necessity. "Architecturally correct" is the option that fits long-term
  structure - sometimes that is the smaller change, sometimes the larger; the
  rule is about reasoning on fit, not on size in either direction.

  When recommending the smaller of competing options, the rationale must
  reference a long-term architectural property of the codebase (module boundary,
  wire format, persisted shape, dependency direction, release-cadence
  separation, code-owner separation) - not a tactical property of the change
  (size, churn, risk of breakage, "captures most of the benefit", "minimal /
  surgical / conservative path"). Tactical phrasings like those are a tell that
  the reasoning is size-based, not fit-based; treat them as exemplars rather
  than an exhaustive list and stop to re-evaluate whenever your reasoning leans
  on any equivalent framing.

  Present the architecturally-correct option as the recommendation; the user can
  still choose the conservative path, but they should choose it knowingly, not
  because the recommendation pre-baked the bias. The user's described scope sets
  the **outer** bound on what gets recommended - if the architecturally-cleaner
  option exceeds that scope, present the in-scope option as the recommendation
  **and** call out the larger option as out-of-scope work the user can choose to
  fold in.

  This rule applies to the option-weighing / recommendation phase only; once a
  plan is approved, section 9 Scope Discipline still governs execution. It does
  **not** alter section 8 PR-review-feedback handling: reviewer comments
  proposing refactors remain out-of-scope per section 8 unless the user has
  authorized expanding the PR's scope.

  Rubber-duck critic findings (e.g., a `:architect` "file Y as a separate
  follow-up issue" remark) carry no special authority here. They are proposals
  to be evaluated on merit per the existing section 11 `ADOPT / SET ASIDE / OUT
  OF SCOPE` classification - the *architectural* reason for separation is what
  matters (independent module, different wire format, different release cadence,
  different code owner). A bare scope-separation verdict without a substantive
  architectural reason is not a recommendation argument.
- Critical thinking applies to your own prior recommendations too. If new
  evidence (test output, file contents, a rubber-duck review, a user correction)
  suggests an earlier suggestion was wrong, acknowledge it explicitly and
  revise.
- **Parallelize plan execution.** Once a plan is approved, treat its steps as a
  dependency graph, not a list. Dispatch every step with no unmet dependencies
  to a parallel sub-agent - including waves further down the graph (e.g., if
  step 1 unblocks steps 2-9, run steps 2-9 in parallel after step 1 finishes,
  not serially). Only respect true dependencies: file conflicts,
  read-after-write on shared state, or an earlier step's output feeding a later
  step. Each sub-agent is stateless, so include the relevant plan slice, repo
  conventions (`AGENTS.md`, `docs/design.md`), and the definition-of-done checks
  it must satisfy. After each wave completes, re-evaluate which steps are now
  unblocked and fan those out too. Use whatever parallel sub-agent mechanism the
  current runtime exposes (e.g., `/fleet` in Copilot CLI; the `Task` tool in
  Claude Code).
- **Parallelize independent validation checks.** After making changes, run the
  section 7 Definition-of-Done checks (lint, test, build) in parallel by issuing
  them in the same response with distinct shell sessions, rather than serially.
  They read the working tree but do not write to it, so there is no contention.
  Use orchestrator-level parallelism (separate shell IDs in one response) rather
  than sub-agents - the output is just pass/fail and the orchestrator has to
  read each result anyway, so spinning up a sub-agent per check is pure
  overhead. Caveats: do not run two dependency installs in parallel against the
  same dependency directory (mutates shared state); if a test flakes under CPU
  contention, re-run sequentially before treating it as a real failure.
