# 0002 — C# on .NET 10

**Status:** Accepted · 2026-09-27

## Context

`design.md` never names a language or runtime, though it leaks an assumption: §7.5
specifies `File.Replace` for atomic writes, which is a .NET API, and the whole design
targets Windows-only Core Audio.

The candidates were C#/.NET, Rust with `windows-rs`, and C++/Win32. All three can reach
WASAPI. The differentiator is not capability but how much of the design's *testability*
argument survives — §6 makes the state machine a pure reducer specifically so arbitration
is unit-testable without sockets or audio devices, and that argument is worth protecting.

An initial pass chose .NET 8. That was wrong: .NET 8 reaches end of support on
10 November 2026, six weeks after this decision, which would have started a greenfield
project on a runtime already out of support before phase 1 could plausibly finish.

## Decision

C# on **.NET 10** (LTS, supported to November 2028).

`SoloSpeaker.Core` targets `net10.0` — platform-neutral, so the reducer physically cannot
reference anything Windows-specific. `SoloSpeaker.App` targets `net10.0-windows`.

## Consequences

- The pure-reducer boundary is enforced by the compiler rather than by discipline.
- Mature test tooling, and `IClock`-style seams are idiomatic.
- Self-contained publish removes any runtime prerequisite on either machine, so the
  choice of runtime version does not become an install-time dependency.
- COM interop for WASAPI is more ceremony than in C++, mitigated by
  [0005](0005-cswin32-for-wasapi-interop.md).
- ~49 MB per machine for a self-contained build. Irrelevant at this deployment size.

## Reversibility

Poor. This is a rewrite, not a migration. The runtime *version* is cheap to move
(`net10.0` → `net12.0` is a property change); the language is not.
