# 0007 — `SoloSpeaker` as the name everywhere

**Status:** Accepted · 2026-09-27

## Context

The repository is `solo-speaker` and the README calls the product `solo-speaker`. The
design document is titled "Proximity Mute Coordinator" and writes its persisted state to
`%LOCALAPPDATA%\MuteCoordinator\`.

That disagreement is not cosmetic. The ledger path is part of §7.3's recovery contract and
appears in the `--restore` path, the uninstall script, and any instruction given to a user
whose machine is unexpectedly silent. Two names for one folder is exactly the ambiguity
that makes a recovery instruction fail.

## Decision

`SoloSpeaker` everywhere: product name, assembly name, executable, root namespace, and the
on-disk folder `%LOCALAPPDATA%\SoloSpeaker\`.

`design.md` is updated to match. It is the older document, but it is the one with the
smaller footprint — the repository name, remote URL, and README all already say
`solo-speaker`.

## Consequences

- One name in the code, the docs, the scripts, and on disk.
- `docs/design.md` is edited, which is otherwise avoided: it is a reviewed document at
  revision 2 and its content should not drift. The change is confined to the title and
  path strings, and touches no design decision.
- `MuteCoordinator` will still appear in the git history of `design.md`. Anyone reading a
  pre-rename revision and following its path instructions would look in the wrong place,
  which is a reason to do this rename now rather than after either machine has ever
  written state.

## Reversibility

Cheap now, expensive later. Once either machine has written
`%LOCALAPPDATA%\SoloSpeaker\config.json`, a rename means a migration path or a repeat of
the pairing ceremony. That is precisely why it is being decided before any code exists.
