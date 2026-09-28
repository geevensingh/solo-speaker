# Architecture Decision Records

Short records of decisions that are expensive to reverse or easy to re-litigate.

`design.md` already does most of this work — §4 records a rejected approach specifically
so it does not get re-proposed, and §9.1 tabulates three decisions with their rationale
and reversibility. The ADRs here extend that habit to the decisions the design does not
cover, and restate §9.1's three so that all of them live in one place.

| # | Decision | Status |
|---|---|---|
| [0002](0002-csharp-on-dotnet-10.md) | C# on .NET 10 | Accepted || [0003](0003-single-file-exe-packaging.md) | Self-contained single-file exe, no installer | Accepted |
| [0004](0004-winforms-notifyicon-for-tray.md) | WinForms `NotifyIcon` for the tray | Accepted |
| [0005](0005-cswin32-for-wasapi-interop.md) | CsWin32 for Win32 and COM interop | Accepted |
| [0006](0006-xunit-and-two-node-harness.md) | xUnit, plus a two-node in-process harness | Accepted |
| [0007](0007-solospeaker-naming.md) | `SoloSpeaker` as the name everywhere | Accepted |
| [0008](0008-external-unmute-is-a-manual-claim.md) | External unmute is a manual claim (design D-1) | Accepted |
| [0009](0009-safety-signal-is-not-debounced.md) | Safety signal is filtered but not debounced (design D-2) | Accepted |
| [0010](0010-mic-in-use-as-call-proxy.md) | Mic-in-use is the proxy for in-a-call (design D-3) | Accepted |
| [0011](0011-pairing-bundle-file.md) | Pairing by bundle file, with in-band roster completion | Accepted |
| [0012](0012-single-instance-guard.md) | Single-instance guard, acquired before ledger replay | Accepted |
| [0013](0013-dpapi-protects-pairkey-only.md) | DPAPI protects `pairKey` only | Accepted |
| [0014](0014-tray-icons-by-shape.md) | Tray icons differentiated by shape, not colour | Accepted |
| [0015](0015-local-rolling-log.md) | A local rolling log, with aggregated ingress drops | Accepted |
| [0016](0016-goodbye-datagram-in-v1.md) | The `bye` field goes into wire format v1 | Accepted |

0011 through 0016 close the six open items that `implementation-plan.md` §8 could not.
0016 is the only one that amends `design.md` itself.

## Format

Context, Decision, Consequences, Reversibility. Short. A record that takes a page to say
what was chosen is a design document wearing the wrong hat.

Number sequentially, never renumber, and never delete. Supersede by adding a new record
that links back to the old one, and mark the old one Superseded. A decision that was
reversed is more useful to a future reader than one that was quietly removed.
