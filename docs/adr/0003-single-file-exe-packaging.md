# 0003 - Self-contained single-file exe, no installer

**Status:** Accepted - 2026-09-27

## Context

`design.md` §7.3 states that "the uninstaller invokes `--restore`", and §7.6 lists "app
uninstalled -> uninstaller runs `--restore`" as a named unmute path. But no installer is
specified anywhere in the design. That constraint - an uninstall step that must run code
*before* the binary disappears - turns out to be the deciding factor.

Three options were compared: a self-contained exe with install/uninstall scripts; an Inno
Setup or WiX MSI; and MSIX.

**MSIX was eliminated on the design constraint.** It cannot run custom uninstall actions
at all, so §7.3's recovery path would be unimplementable. It also virtualizes
`%LOCALAPPDATA%`, which undermines the ledger's "recoverable by hand after the app is
gone" property (Goal 8), and it requires a signing certificate from day one.

## Decision

`dotnet publish` to a self-contained, single-file, untrimmed `win-x64` executable, plus
`scripts/install.ps1` and `scripts/uninstall.ps1`.

`uninstall.ps1` invokes `SoloSpeaker.exe --restore` **before** deleting the binary. Order
is the whole point.

`install.ps1` registers a Task Scheduler logon task rather than a `Run` key entry, with a
30 s delay: startup ledger replay needs the audio endpoint enumerable, and a `Run`-key
launch races the audio service at logon. A failed enumeration raises `error` (§7.4), so
the cheaper option would produce an error icon on every boot.

## Consequences

- No installer tooling in the build. The CI artifact *is* the deliverable.
- No .NET runtime prerequisite on either machine.
- The uninstall path is code we own, so §7.3's contract is cheap to keep honest and is
  linted in CI.
- No Add/Remove Programs entry. Deleting the folder by hand strands a muted endpoint with
  no `--restore` available.
- No auto-update. Updates are "stop both, update both, start both", which is the right
  procedure anyway - it avoids the mixed-version wire window entirely.

The missing Add/Remove entry is acceptable because Goal 8's real failsafe is the Windows
volume mixer, not `--restore`. A hand-deleted install costs one manual unmute, not a
stranded machine.

## Reversibility

Good. Inno or WiX would wrap the same executable and call the same `--restore` switch,
with no change to the application. Move when an Add/Remove Programs entry or a real update
story is wanted. Moving to MSIX would still require redesigning §7.3.
