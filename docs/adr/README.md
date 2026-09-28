# Architecture Decision Records

Short records of decisions that are expensive to reverse or easy to re-litigate.

`design.md` already does most of this work — §4 records a rejected approach specifically
so it does not get re-proposed, and §9.1 tabulates three decisions with their rationale
and reversibility. The ADRs here extend that habit to the decisions the design does not
cover, and restate §9.1's three so that all of them live in one place.

| # | Decision | Status |
|---|---|---|
| [0002](0002-csharp-on-dotnet-10.md) | C# on .NET 10 | Accepted |
| [0003](0003-single-file-exe-packaging.md) | Self-contained single-file exe, no installer | Accepted |
| [0004](0004-winforms-notifyicon-for-tray.md) | WinForms `NotifyIcon` for the tray | Accepted |
| [0005](0005-cswin32-for-wasapi-interop.md) | CsWin32 for Win32 and COM interop | Accepted |
| [0006](0006-xunit-and-two-node-harness.md) | xUnit, plus a two-node in-process harness | Accepted |
| [0007](0007-solospeaker-naming.md) | `SoloSpeaker` as the name everywhere | Accepted |
| [0008](0008-external-unmute-is-a-manual-claim.md) | External unmute is a manual claim (design D-1) | Accepted |
| [0009](0009-safety-signal-is-not-debounced.md) | Safety signal is filtered but not debounced (design D-2) | Accepted |
| [0010](0010-mic-in-use-as-call-proxy.md) | Mic-in-use is the proxy for in-a-call (design D-3) | Accepted |

## Format

Context, Decision, Consequences, Reversibility. Short. A record that takes a page to say
what was chosen is a design document wearing the wrong hat.

Number sequentially, never renumber, and never delete. Supersede by adding a new record
that links back to the old one, and mark the old one Superseded. A decision that was
reversed is more useful to a future reader than one that was quietly removed.
