# 0004 — WinForms `NotifyIcon` for the tray

**Status:** Accepted · 2026-09-27

## Context

`design.md` §7.4 requires a tray icon with five distinct states, a context menu including
the denylist discovery affordance, balloon notifications, left-click handling, and sticky
error tooltips. There is no other UI in the product — no window, no settings dialog.

Options: WinForms `NotifyIcon`, WPF with a third-party tray component such as
`H.NotifyIcon`, or raw `Shell_NotifyIcon` interop.

## Decision

WinForms `NotifyIcon`.

## Consequences

- Everything §7.4 asks for is built into the BCL: icon, tooltip, `ContextMenuStrip`,
  `ShowBalloonTip`, click events. No third-party dependency for the only UI surface.
- A message pump exists, which `RegisterHotKey` needs anyway — `WM_HOTKEY` is delivered to
  a window, so a hidden WinForms window serves both the hotkey and the tray.
- The tray is the only visible explanation for why a machine is silent, so keeping it on
  the least exotic available technology is a reliability choice as much as a convenience
  one.
- WinForms is not a good fit for rich UI. There is no rich UI here, and if one is ever
  wanted this decision should be revisited rather than stretched.
- Requires `UseWindowsForms` and a `net10.0-windows` target on `SoloSpeaker.App`. No
  effect on `SoloSpeaker.Core`, which stays platform-neutral.

## Reversibility

Good, and well contained. The tray lives entirely in `SoloSpeaker.App`, below the
`SoloSpeaker.Core` boundary, so swapping it touches no arbitration logic and breaks no
test.
