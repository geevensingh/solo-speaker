# 0014 - Tray icons differentiated by shape, not colour

**Status:** Accepted - 2026-09-27
**Resolves:** `implementation-plan.md` §8.4

## Context

`design.md` §7.4 requires five tray states and says the tray is "the only visible
explanation for why a machine is silent". No icon assets exist or are specified.

The temptation is to draw one speaker glyph and tint it five ways. At 16 px, in a tray
that may be on a light or dark taskbar, colour is close to useless - and it fails
completely for a colour-blind user.

`muted` and `alone` are the pair that must never be confused, because they are the two
states that answer the question the user actually has: *is this machine silent because the
system decided so, or because something is broken?*

## Decision

Five icons differentiated by **silhouette first**, with colour as reinforcement only:

| State | Silhouette |
|---|---|
| `active` | Filled speaker with sound waves |
| `muted` | Speaker with a slash through it |
| `alone` | Speaker outline, no waves, no slash - a distinctly emptier shape than `muted` |
| `quarantine` | Speaker with a dashed outline |
| `error` | Speaker with an exclamation badge |

SVG sources in `assets/tray/`, with multi-resolution `.ico` files (16/20/24/32/48) checked
in beside them. No build-time conversion step.

Tooltips always name the state. The `error` tooltip names the specific cause, per §7.4.

## Consequences

- Distinguishable at 16 px, on either taskbar theme, without relying on hue.
- `muted` and `alone` differ in silhouette, not just in tint.
- Checking in both the `.ico` and its SVG source avoids a conversion tool in the build,
  at the cost of remembering to regenerate after editing. For five icons that never
  change, that is the cheaper trade.
- A test asserts that every `TrayState` value maps to a distinct embedded resource, so a
  missing or duplicated icon fails in CI rather than in the tray.

## Reversibility

Trivial. They are image files.
