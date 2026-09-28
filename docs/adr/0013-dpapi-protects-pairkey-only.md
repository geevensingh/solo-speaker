# 0013 - DPAPI protects `pairKey` only

**Status:** Accepted - 2026-09-27
**Resolves:** `implementation-plan.md` §8.3

## Context

`design.md` §7.5 puts `pairKey` - the 256-bit secret the entire authentication scheme
rests on - in `config.json` in cleartext, alongside `pairId`, the roster, port, hotkey,
denylist, and debounce. The design does not discuss protecting it.

DPAPI (`ProtectedData`, `CurrentUser` scope) would encrypt it for very little code. But
encrypting the whole file has a cost the design does care about: §7.5 contemplates
`config.json` and `state.json` being restored from backup, and builds a cross-file
`pairId` check specifically so a half-restore raises `error` instead of silently breaking
§5.5's invariant. That check assumes the file can be inspected. A wholly encrypted config
is also undiagnosable by hand, which is a poor fit for a design whose Goal 8 is
recoverability by hand.

## Decision

Protect **only** the `pairKey` field. `config.json` stores `pairKeyProtected` - base64 of
`ProtectedData.Protect(pairKey, entropy: pairId, DataProtectionScope.CurrentUser)` - and
every other field stays plaintext.

A decrypt failure raises `error` with cause "configuration not readable on this profile;
re-pair required". It is never a crash and never a silent fallback.

## Consequences

- The secret is protected at rest against another user on the machine, and against the
  file being copied off it.
- `config.json` remains human-readable and hand-diagnosable, so §7.5's cross-file `pairId`
  check and the §10 recovery tests still work by inspection.
- `config.json` cannot be moved to another user profile or machine. That is consistent
  with the design rather than a new limitation: §7.7 already names re-pairing as the
  supported path for replacing a machine, and the roster entry of a replaced machine is
  exactly the condition §5.5's positive predicate exists to catch.
- `pairing.json` is deliberately **not** protected - it has to be readable on the other
  machine. That is precisely why [0011](0011-pairing-bundle-file.md) deletes it on both
  sides as soon as the ceremony completes.
- Using `pairId` as the DPAPI entropy binds the ciphertext to the pairing, so a
  `config.json` from an older pairing on the same profile fails to decrypt rather than
  silently producing a key for the wrong pair.

## Reversibility

Good, in the direction that matters. Dropping protection is trivial. Adding protection
later would require a migration on both machines, which is why it is being decided now.
