# Manual Test Matrix

Everything in `design.md` §10 that CI cannot prove, plus the paths added by
[`implementation-plan.md`](implementation-plan.md) §4.6.

**A release is gated on CI green *and* a completed run of this matrix.** CI on its own is
a weaker claim than it looks here: the reducer is fully covered in-process, but every
mechanism that actually makes a machine silent - WASAPI actuation, capture-session
enumeration, subnet broadcast, sleep and resume, device change, hotkey registration - is
only ever exercised by hand.

## How to run

Copy this file to `docs/signoff/<version>-<date>.md` and fill in Result and Notes. Do not
edit this file to record a run.

Two machines, both paired, both on the same subnet. `D` is the desktop, `L` the laptop.
Unless a row says otherwise, start from: both running, both audible, `D` the owner.

A row fails if the observed behaviour differs from Expected **or** if the tray state is
wrong. The tray is the only visible explanation for why a machine is silent (§7.4), so a
correct mute with a wrong icon is still a failure.

---

## A. Core arbitration

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| A1 | Hotkey on `L` | `L` audible and `active`; `D` muted and `muted` | | |
| A2 | Hotkey on `D`, then `L`, then `D`, ten times | Ownership follows every press; no flapping, no stuck state | | |
| A3 | Tray left-click on `L` | Same as A1 - the three input surfaces are one event | | |
| A4 | Hotkey on the machine that already owns | No-op. Nothing changes anywhere | | |
| A5 | Press the hotkey on both machines within ~100 ms | Both converge to the *same* owner, by lexicographically smaller roster ID. Check both trays | | |
| A6 | `L` owner. Close `L`. Wait 15 s | `D` unmutes within the 10 s presence window, tray `alone` | | |
| A7 | Reopen `L`. Wait for quarantine to expire | `L` is still owner; `D` mutes again. Stored ownership survived absence | | |
| A8 | `L` owner, `D` muted. Exit `L` **gracefully** from the tray menu | `D` unmutes in **under a second**, not after 10 s. This is the `bye` datagram | | |
| A9 | Repeat A8 but hard-kill `L` instead | `D` takes the full 10 s window. Confirm the log distinguishes this from A8 | | |
| A10 | `L` owner. Shut Windows down on `L` normally | Same as A8 - `bye` is sent on `WM_ENDSESSION`, not only on tray exit | | |
| A11 | Start a Windows shutdown on `L`, then **cancel it** | `L` keeps running. It must not have announced a departure it did not make; presence re-establishes on the next heartbeat | | |
| A12 | `L` claims with the hotkey and is immediately shut down | `D` adopts the claim from the state datagram. The `bye` that follows must **not** move ownership | | |

## B. Stickiness - the central property

Per §5.2, ownership is a latch. These rows exist because the most likely regression is
someone "helpfully" making it a function of current conditions.

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| B1 | Claim on `D`. Take a call on `L`. End the call | `L` remains owner after the call ends. `D` stays muted | | |
| B2 | Claim on `D`. Type on `L` for five minutes without claiming | `D` is still owner. Idle time is never consulted | | |
| B3 | Claim on `D`. Lock `L`, then unlock it | No ownership change | | |
| B4 | Claim on `D`. Switch foreground apps on both repeatedly | No ownership change | | |

## C. Rejoin and quarantine

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| C1 | `L` owner. Sleep `L`. Claim on `D`. Wake `L` | **`L` does not steal the mute back.** `D` stays owner; `L` adopts `D`'s state despite holding a higher `seq` | | |
| C2 | Close `L`'s lid, wait 5 min, open it | No ownership movement. This is the row §7.6 exists for | | |
| C3 | Reboot both simultaneously | Both quarantine, both audible, converge on expiry. Neither is left muted | | |
| C4 | Sleep `L`. Wake it and immediately press the hotkey, inside the 12 s window | Claim wins immediately; quarantine exits. A machine booting into a meeting must not mute itself | | |
| C5 | Disconnect and reconnect `L`'s Wi-Fi | Quarantine window restarts on the network-change notification | | |
| C6 | `L` wakes and sees `D`'s heartbeats, then `D` shuts down gracefully mid-window | **`L` still adopts `D`'s state at expiry.** The observation latch is set and `D`'s parting `bye` must not clear it. This is the B-2 case, and it needs no attacker | | |
| C7 | Suspend `L` mid-mute for over an hour, then resume | Audio state correct; no stuck mute; tray accurate | | |

## D. Audio endpoints and the ledger

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| D1 | While `D` is muted, swap `D` from speakers to a USB/Bluetooth headset | New endpoint is muted; **the old endpoint is released**, not left stranded | | |
| D2 | Swap back | Same, in reverse. Check the volume mixer for both devices | | |
| D3 | While `D` is muted, unmute it from the Windows volume flyout | Honoured as a manual claim (D-1). `D` becomes owner; `L` mutes. No flapping | | |
| D4 | Repeat D3 five times quickly | No self-feedback loop. The 250 ms suppression window holds | | |
| D5 | While `D` is muted, hard-kill `SoloSpeaker.exe` (Task Manager -> End task) | `D` stays muted - this is expected and recorded in §7.6 | | |
| D6 | Relaunch after D5 | Ledger replay restores audio on startup | | |
| D7 | Repeat D5, then run `SoloSpeaker.exe --restore` instead of relaunching | Audio restored; app does not start | | |
| D8 | Repeat D5, then run `scripts\uninstall.ps1` | Audio restored before the binary is deleted; script reports success | | |
| D9 | Make `--restore` fail (corrupt the ledger), then run `uninstall.ps1` | **Nothing is removed.** Binary and `ledger.json` both survive, exit code is non-zero, and the message names the repair command. This is the Goal 1 branch | | |
| D10 | Repeat D9 with `-Force` | Removal proceeds, with a warning telling you to confirm audio by hand | | |
| D11 | While `D` is muted, pull its power | On next boot, ledger replay restores audio | | |
| D12 | Plug in a second render device and route audio to it while muted | Second device stays audible. Accepted per §9.2-4; confirm it is not *also* muted | | |

## E. Failure and error states

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| E1 | Register the same hotkey in another app first, then start SoloSpeaker | Tray balloon **and** sticky `error`. A silently dead hotkey is indistinguishable from normal operation until needed | | |
| E2 | Delete `state.json`, keep `config.json`, restart | `error` raised, not silent misbehaviour | | |
| E3 | Edit `config.json` so its `pairId` differs from `state.json`'s | `error` raised | | |
| E4 | Hand-edit `state.json` so `activeOwner` names a machine not in the roster | **Both** machines audible; `error` on the affected one | | |
| E5 | Corrupt `ledger.json` with invalid JSON, restart | `error` raised; app still starts; nothing muted | | |
| E6 | Acknowledge an `error`, then trigger a different one | Tooltip names the *new* specific cause | | |
| E7 | Disable the audio endpoint in Sound settings while running | `error` on enumeration failure; no crash | | |
| E8 | Launch a second copy by hand while one is already running | Second exits with a balloon and **touches nothing** - no ledger write, no endpoint change. Verify by muting first, then launching, then confirming the mute survives | | |
| E9 | Run `--restore` while an instance is running | Refuses with "SoloSpeaker is running; exit it first". Does not replay the live ledger | | |
| E10 | Copy `config.json` to a second Windows user profile and start there | `error` with a re-pair cause. No crash, no silent fallback to an unprotected key | | |

## F. Network and hostile input

F2-F5 need a small sender script; see [`wire-format.md`](wire-format.md) for the golden
vectors to mutate.

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| F1 | Put `D` and `L` on different subnets while physically adjacent | Both stay audible. Recorded as §9.2-1's opposite direction | | |
| F2 | Send a datagram with a wrong `pairId` | Dropped silently. No state change, no `error` | | |
| F3 | Send a correct `pairId` with an invalid `mac` | Dropped. This is the case revision 1's HMAC could not catch | | |
| F4 | Send a valid `mac` with a `machineId` outside the roster | Dropped | | |
| F5 | Send a valid datagram with `seq = uint64.Max` | Dropped, `error` raised. **Persisted state on both machines is unchanged** | | |
| F6 | Replay a captured valid datagram inside the presence window | Accepted per §7.1. Confirm the blast radius stays bounded | | |
| F7 | Replay a captured `bye` datagram | Peer presence clears and the machine unmutes - the safe direction. **`activeOwner` must not move** | | |
| F8 | Replay a captured `bye` whose `seq` is higher than the receiver's | Still no ownership change. A `bye` bypasses §5.4 in both directions of ordering | | |
| F9 | Flood replayed `bye`s at ~1 Hz during a rejoin window | The quarantine observation latch holds; the rejoining machine adopts its peer's state at expiry rather than asserting stale ownership | | |
| F10 | Send 5,000 malformed datagrams in one minute | Log shows one aggregated line per reason, not 5,000 lines | | |
| F11 | Leave `L` in another room on the same Wi-Fi | `L` is treated as present and will be muted. Known limitation §9.2-1 - confirm it is the *only* surprise | | |

## G. Install, update, uninstall

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| G1 | Clean install on a machine with no prior state | Installs, registers the logon task, prints pairing instructions | | |
| G2 | Reboot after G1 | Starts automatically after the delay; tray appears | | |
| G3 | Full pairing ceremony from scratch on both | Both hold identical `pairId`, `pairKey`, and roster. **Fingerprints match on both screens** | | |
| G4 | Before pairing completes, try to mute | Neither machine ever mutes. No complete roster means §5.5 is false | | |
| G5 | Run `--pair-init`, then wait out the 10-minute window without joining | Pairing mode expires; `error` with cause "pairing incomplete"; machine never mutes | | |
| G6 | Confirm `pairing.json` is gone from both machines after G3 | Deleted on both sides. It is the only place `pairKey` exists in cleartext | | |
| G7 | Run `install.ps1` over a running instance | Running instance stops gracefully and restores audio *before* the binary is replaced | | |
| G8 | Stop both, update both, start both | No mixed-version window; normal operation resumes | | |
| G9 | Update one machine only, with an incompatible wire version | Peer sees no peer, both audible. **The log must show a bad-MAC or version-mismatch drop rate, distinguishing this from an absent peer** | | |
| G10 | `uninstall.ps1` while muted | Audio restored, task removed, `config.json` deleted | | |
| G11 | `uninstall.ps1 -KeepConfig`, then reinstall | Pairing survives; no second ceremony needed | | |

## H. Phase 2 only - call detection

Skip entirely while `micLive` is hardcoded `false`.

**Run H5 first.** It is the empirical check for `design.md` §9.2-3, the assumption that
render-mute does not disturb capture sessions. The whole auto-claim loop rests on it, and
a standalone pre-phase-1 spike was deliberately declined in favour of this row - so if it
fails, it fails here, and the rest of phase 2 is built on sand until it is resolved.

| # | Steps | Expected | Result | Notes |
|---|---|---|---|---|
| H1 | Join a Teams call on `L` | `L` claims ownership without the hotkey; `D` mutes | | |
| H2 | Sit on a Teams pre-join screen on `L` for 30 s, then leave without joining | `L` is audible the whole time (safety signal), but **ownership never moves** | | |
| H3 | `L` owner and in a call. Press the hotkey on `D` | `L` stays audible - `selfMicLive` overrides the latch. Goal 2 | | |
| H4 | Both machines in calls simultaneously | Both audible. Ownership resolves underneath | | |
| H5 | `D` muted, then join a call on `D` | Claim still fires. **This is §9.2-3**; if it fails, the auto-claim loop is broken | | |
| H6 | Run an always-on mic consumer (NVIDIA Broadcast, Krisp) on `L` | `L` becomes permanently unmutable. Confirm the denylist discovery UI lists the process | | |
| H7 | Add that process to the denylist via the tray menu | `L` becomes mutable again without a restart | | |
| H8 | Start a call, end it, start another within 10 s | Debounce behaves; no spurious ownership churn | | |

---

## Sign-off

| Field | Value |
|---|---|
| Version | |
| Date | |
| Desktop (`D`) - Windows build | |
| Laptop (`L`) - Windows build | |
| Rows passed / run | |
| Rows skipped (and why) | |
| Signed | |
