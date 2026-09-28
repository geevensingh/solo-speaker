# 0005 - CsWin32 for Win32 and COM interop

**Status:** Accepted - 2026-09-27

## Context

The design needs a specific and bounded set of native surface: `IMMDeviceEnumerator`,
`IMMDevice`, `IAudioEndpointVolume`, and `IMMNotificationClient` for the MuteActuator
(§7.3); `IAudioSessionManager2`, `IAudioSessionEnumerator`, and `IAudioSessionControl2`
for the MicWatcher (§7.2); and `RegisterHotKey` / `UnregisterHotKey` for manual claim
(§7.4).

Options: `Microsoft.Windows.CsWin32` (source generator over official Win32 metadata),
NAudio (a hand-written managed wrapper that already covers WASAPI), or hand-rolled
`DllImport` and COM interface declarations.

## Decision

CsWin32, driven by an annotated `src/SoloSpeaker.App/NativeMethods.txt`.

## Consequences

- Signatures are generated from official Win32 metadata, so the class of bug where a
  hand-transcribed COM vtable or marshalling attribute is subtly wrong does not arise.
- No runtime dependency: it is a source generator, so nothing ships with the app.
- `NativeMethods.txt` becomes a reviewable inventory of exactly which native surface the
  app touches. Each entry is annotated with the design section that needs it, so an entry
  with no remaining justification is visible at review time.
- CsWin32 validates the list at build time. It caught two errors in the very first
  scaffolded version of that file, before any product code existed.
- Generated WASAPI COM code is more verbose at the call site than NAudio's wrappers. Some
  thin helpers over the raw interfaces will be wanted, and those helpers are where the
  `IMuteActuator` seam already sits.
- NAudio would have been faster to start with, but adds a dependency whose abstractions
  cover far more than this app needs, and whose behaviour would sit between the app and
  the exact `SetMute` semantics §7.3 depends on.

## Reversibility

Good. All interop lives behind `IMuteActuator` and `IMicWatcher` in `SoloSpeaker.App`.
Swapping the implementation changes nothing in `SoloSpeaker.Core` and breaks no test,
because no test crosses that seam.
