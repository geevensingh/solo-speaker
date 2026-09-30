# 0012 - Single-instance guard, acquired before ledger replay

**Status:** Accepted - 2026-09-27
**Resolves:** `implementation-plan.md` §8.2

## Context

Nothing in `design.md` prevents two copies running on one machine, and the deployment
makes it reachable in ordinary use rather than only in testing: `install.ps1` registers a
logon task, and nothing stops the user launching the exe by hand as well.

Two instances would both reconcile the same endpoint every tick (§7.3) and both write the
mutation ledger. That is a live-lock on the mute state and a corrupted recovery record -
and the recovery record is the entire crash-recovery story.

## Decision

A named mutex, `Local\SoloSpeaker`, acquired at startup. If it is already held, show a
tray balloon, exit with a non-zero code, and **touch nothing** - no ledger, no endpoint,
no state file.

`--restore` takes the same mutex. If it is held, it refuses with "SoloSpeaker is running;
exit it first" rather than replaying a ledger that a live instance is actively using.

**Amendment - work item 6, 2026-09-29.** The fixed name above is no longer the complete
name. `Local\SoloSpeaker` remains the product prefix, but the runtime mutex name is derived
from the resolved data root so two instances with different roots do not contend. Work item
6 ships only that identity helper (`Hosting/InstanceIdentity.cs`), because its
two-instance UDP harness depends on it. Work item 12 still ships acquisition, the tray
balloon, the non-zero exit code, and the shutdown channel.

**Amendment - work item 7, 2026-09-30.** Work item 7 now ships mutex acquisition whole:
both the launch path and `--restore`. Work item 7 implements ledger replay at startup, so a
second launch would otherwise replay and clear the first instance's ledger while that
instance still holds a live mute, leaving the mute with no recovery record. That is exactly
the scenario this ADR's Context describes, and manual matrix row E8 exists to prove it. One
mutex acquisition is a single invariant and gets a single site. Work item 12 still owns the
tray balloon, the documented exit-code UX, the shutdown channel, and packaging - all of
which need a tray that does not exist yet.

**The guard is acquired before ledger replay, not after.** This ordering is the whole
point of the record. §7.3 requires ledger replay to run "on every startup, before anything
else", but a second instance starting while the first has legitimately muted an endpoint
would replay the ledger, restore the endpoint to `priorMute`, clear the entry - and leave
the first instance believing it still holds a mute whose recovery record no longer exists.
A hard kill after that point strands the endpoint permanently. The guard must therefore
come first, and §7.3's "before anything else" means "before anything else that this
instance is entitled to do".

## Consequences

- Second launches against the same data root fail fast and visibly rather than silently
  corrupting recovery state.
- Test launches against different data roots can coexist, which keeps the row 6
  two-instance UDP harness valid after the guard lands.
- `Local\` rather than `Global\`: the design assumes one interactive user (§9.2-6), and a
  per-session mutex is the honest scope. Fast-user-switching and RDP remain out of scope.
- The balloon matters. A second launch that exits silently looks like the app failed to
  start, which invites the user to try again.

## Reversibility

Trivial. A dozen lines in the composition root.
