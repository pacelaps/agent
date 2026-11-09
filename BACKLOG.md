# Agent backlog

Spec section: "Agent specification". Tick items as they are done. Add new findings at the bottom.

## Step 1: proof of concept and spikes

### 1.0 Project setup
- [x] Solution with Core, IRacing, Console and Tests projects (.NET, Windows x64). Pin IRSDKSharper. (.NET 10, IRSDKSharper 1.3.0, central package versions)
- [x] CI build and test (GitHub Actions on a Windows runner). `.github/workflows/ci.yml`; first run green on 2026-10-25 (run #1, commit fe12ef2).
- [x] Logging to file. (Serilog, rolling daily in `%LOCALAPPDATA%\PaceLaps\Agent\logs`)

### 1.1 Spikes 1 to 3 first (they decide whether the design holds)
- [x] Spike 1: macros run admin commands without iRacing focused (20 repetitions). Pass, 20/20 (2026-10-25).
- [x] Spike 2: typed chat reliability (50 messages, borderless windowed and fullscreen). Pass (50/50, median 300 ms, focus 50/50). Exclusive fullscreen untested on the operator's triple-screen setup.
- [x] Spike 3: admin commands in an AI hosted session (!yellow, !restart single/double, !pacelaps, !waveby, !pitclose, !pitopen, !black, !clear, !eol). Pass; `!restart single` confirmed by the operator's own later test.
- [x] **Stop and report to the user** after spikes 1 to 3 with results and recommended design changes. Reported 2026-10-25, with proposals for the specification.

### 1.2 Remaining spikes
- [x] 4 Incident count latency: pass, driver list about 0.6 s (results positions not live)
- [x] 5 Per-car live fields: pass
- [x] 6 Replay jump while live: pass; re-check whether per-car telemetry follows a replay (spike6 now samples all cars)
- [x] 7 Maximum chat message length: about 254; agent limit 250
- [x] 8 Operator name and session admin detection: pass
- [x] 9 Caution extension (!yellow at one lap to go, !pacelaps +n and absolute): pass; cap 20; !pacelaps only after the first caution lap
- [x] 10 Macro configuration can be read: pass (app.ini [Autochat Messages]); key bindings binary
- [ ] 11 Session identity fields (league, season, session, sub-session): open, needs a league session (ids read in an official session)
- [x] 12 Harmless command to verify the typed channel: !help (sender's view; others untested)
- [x] 13 Caution without the AI pace car: fail (no option); fallback applied
- [x] 14 Spotter and Crew Chief reaction to iRacing flags: pass (Crew Chief and iRacing's built-in spotter)
- [x] 15 (added) Chat Enter reliability and chat readback: partial, not reproduced (34/34 sent), readback works

### 1.3 Proof-of-concept program
- [x] State machine: Sleeping, Self-test, Ready, Active. Ready = self-test passed, can start or join a session. Active = part of a session on the server, sending and receiving data (the server binds the agent to a new or existing session; automatic). Operator can go back to Sleeping any time. Failed self-test returns to Sleeping with the exact failure. (`Core/States`, unit-tested.)
- [x] Self-test steps 1 to 6 from the spec, readiness per channel. Passed the acceptance run (2026-10-25, `docs/poc-acceptance.md`). Uses the spike results: DriverIsAdmin checked before typing (spike 8), `!help` (spike 12), macro slots read from app.ini with the operator as fallback (spike 10).
- [x] Session identity and operator identity reporting. Passed the acceptance run (2026-10-25, `docs/poc-acceptance.md`). Identity in snapshots and session_change events, operator in hello; league override when the league id is 0 (spike 11 fallback).
- [x] Command renderer: allow-listed templates and parameter validation for the whole command catalogue. Unit tests per command. (Chat line limit 250 from spike 7, pace laps 1 to 20 from spike 9; black flag with laps rejected until its syntax is confirmed.)
- [x] Typed channel with queue, focus handling and timing. Acceptance run 1: `!eol #7` stayed in the chat box twice (Enter ignored by the sim). Spike 15 did not reproduce it but showed the chat box can be read. Now after every line the chat box is read; exactly our line gets one more Enter; our line mixed with other text, or still there after the second Enter, is cleared and fails the command; other text is cleared and noted. Acceptance run 2 (2026-10-25): every check found an empty box; the retry path is covered by unit tests only.
- [x] Macro channel (optional per agent, slots 1 to 15, text ending in `$`), if spike 1 passes. Passed the acceptance run (2026-10-25, `docs/poc-acceptance.md`). Spike 1 passed.
- [ ] SDK message channel (replay jump), if spike 6 passes. Built (`--replay`); stays unverified until spike 6.
- [x] Snapshot builder (1 to 2 Hz) validated against `snapshot.schema.json`. Passed the acceptance run (2026-10-25, `docs/poc-acceptance.md`). Mappings confirmed by spike 5 (lap = laps completed, connected = in the world).
- [x] Event detection: flag change, session change, incident count deltas with sequence numbers. Passed the acceptance run (2026-10-25, `docs/poc-acceptance.md`). Incidents from the driver list (spike 4).
- [x] Spike 5 also records a real snapshot sequence to a file; it becomes test data for the web service's sorting view. (`docs/recordings/spike5-20261025-035810Z-fd3a.jsonl`)
- [x] Result reporting per command: received, executing, then result sent / verified (telemetry shows the effect) / failed with reason. Passed the acceptance run (2026-10-25, `docs/poc-acceptance.md`).
- [x] Clock offset measurement against a server (stub in the PoC). (`ClockOffsetEstimator`, shortest round trip wins.)

### Exit criteria
Every spike has pass or fail. The typed channel works, the macro channel works or is dropped, and the self-test reports ready only on success.

## Step 3: integration (together with the web service)
- [x] Device-code pairing (basic flow, console prompt) and token storage; the server needs it to accept the agent. (`pair --server URL`; DPAPI)
- [x] Connect to the web service: hello, activation, ready state, heartbeats (1 s; three missed = loss). (`run --server URL`, `HttpServerLink`, Pusher client for Reverb, long-poll fallback)
- [x] Live snapshots and events.
- [x] Execute `yellow`, `announce` and `pit_close` from the server with the full status checklist. (live test against the development stack; in the sim: `docs/integration-test.md`)
- [x] Forced disconnect and resync. (`offline SECONDS` in the console; unit tests)
- [x] Test run in the sim with the user (`docs/integration-test.md`): passed 2026-10-26, `docs/integration-results.md`.
- [x] Findings of run 1 (`docs/integration-results.md`): long-poll waits a moment when an answer brings nothing new (3); heartbeats get their own request lane (4).

## Step 5: full agent
Plan approved 2026-10-27: 5.1 contracts and server (web service, done), 5.2 agent core, 5.3 local failsafe, 5.4 tray app, 5.5 packaging, 5.6 sim test script and pilot.

- [x] Tray application: state, Activate/Sleep, self-test results, logs, config file. (5.4: WinForms tray app `pacelaps-agent.exe` on the shared `Hosting/AgentRuntime`; Sleeping now has no server connection, also in the console's `run --server`; status window, settings screen with start with Windows, notifications, one instance. The console is now `pacelaps-agent-cli.exe`. Manual check `docs/tray-check.md` passed in all 10 steps on 2026-10-28; in the sim in 5.6.)
- [x] Device pairing UX in the tray app (5.4: pairing screen with the code in large type; the paired server becomes the settings' server).
- [x] Executor model with epoch numbers. (5.2: epochs through a handover and back tested; an executor whose typed channel fails twice in a row reports it failed, so the server hands over, until the next self-test)
- [x] Event buffer with sequence numbers and resync. (5.2: the resync carries the outage window on the server's clock (`agent.outage_reported` on the server); 1000 events kept, a loss logged)
- [x] Optional macros with slot selection. (5.2: slots in `config.json`; the league's macros policy switches the channel off and back to its verified state)
- [x] Self-test results reported to the server (5.2: `self_test_result` after every self-test the server answered, details without names)
- [x] Opt-in local failsafe: interpreter for offline blocks, copied procedure with version, staggered watchdog, checks caution state before acting. (5.3: copy fetched, checked and kept in `local-failsafe.json`; delay plus 15 s per rank; armed per the league; one run per outage, released by a steward; idempotent commands retried once after 2 s; started, command, ended and released events; simulated outage tested end to end, copy fetch tested live. Still open for 5.6: in the sim, whether repeated `!pacelaps +1` at one lap to green keeps the caution going, and a real outage with `offline`.)
- [x] Packaging: self-contained x64 build. (5.5: one single-file exe in a zip with the operator guide and a SHA-256 checksum, `scripts/publish.ps1`; checked with `--version` in CI; GitHub release on a `v*` tag; unsigned for the pilot, SmartScreen step in the guide. Later: code signing, and hosting the download on the web service together with automatic updates.)

### 5.6 Sim test and pilot
- [x] Full agent test in the sim (`docs/full-agent-test.md`): passed 2026-10-28 with the production service and v0.5.0, `docs/full-agent-results.md`. Resync, handover there and back, local failsafe with three pace-lap extensions (no cap), released by a steward.
- [x] Findings fixed (spec feedback 29 to 33): release builds pinned to the production server; older epochs ignored within a session; restart commands not checked under green; times on the server's clock; a last `sleeping` heartbeat before Sleep (server: first executor automatic, sleep releases at once, pit hint and warning).
- [x] Release v0.5.1 and a short re-check (parts B and D) after the web service deploy: passed 2026-10-28 (`docs/full-agent-results.md`, re-check).
- [ ] Pilot race.

## Later
See the spec's later list (auto-update, camera control, and so on).

## Findings and decisions
(Add dated notes here.)

- 2026-10-25: Typed channel sequence built early in Core (`TypedChatSender`) because spikes 2 and 3 need it; backlog 1.3 reuses it and adds the queue in front. Assumptions to confirm in spike 2: 150 ms wait after begin-chat, 100 ms wait after Enter before focus moves back, 1 s focus timeout. All are command-line options.
- 2026-10-25: Focus is taken with SetForegroundWindow, then an unassigned virtual-key press plus SetForegroundWindow, then AttachThreadInput. Not an Alt press: Alt would open the browser's menu bar.
- 2026-10-25: Text is mapped to scancodes with the keyboard layout of iRacing's input thread. Dead keys, control characters and characters missing from the layout are rejected before chat opens. Free-text validation for announce and message will build on this.
- 2026-10-25: The SDK cannot read chat back, so arrival in chat (spikes 1 and 2) is the operator's observation, recorded in the log. "sent" in the status checklist means keystrokes and Enter reached the sim window.
- 2026-10-25: The spike 3 command syntax (`!black #12 D`, `!restart single`, `!pacelaps +2` and so on) is unconfirmed. It lives in `spike3-steps.json` and can be corrected during the run.
- 2026-10-25: Keystrokes from a non-elevated process to an elevated iRacing are dropped silently (UIPI). The runners check and warn; the self-test (1.3) needs the same check.
- 2026-10-25 (spikes 1 to 3): the confirmed command syntax for the renderer (1.3) is `!black #<car> D`, `!black #<car> <seconds>`, `!clear #<car>`, `!waveby #<car>`, `!eol #<car>`, `!pacelaps <n>`, `!pacelaps +<n>`, `!restart single|double`, `!pitclose`, `!pitopen`, `!yellow [message]`, `!clearall`. `!dq` and `!remove` are listed by `!help` but untested.
- 2026-10-25: Telemetry that shows a command's effect (for the "verified" result): caution bits in `SessionFlags`; black bit in `CarIdxSessionFlags[car]` (works for every car); `wavedAround` and `endOfLine` in `CarIdxPaceFlags[car]`; `PaceMode` for the restart type (updates late, after minutes). `PitsOpen` did not change on `!pitclose`, and no variable shows pace laps: those commands report "sent" only, unless the session info has them (check in spike 5 or 10 by diffing the session info YAML).
- 2026-10-25: "Green" must be derived (no caution, red or checkered bit): the `green` bit is only set for a few seconds after the green flag.
- 2026-10-25: Focus: `AttachThreadInput` was needed for 94 % of sends; plain `SetForegroundWindow` never worked on its own.
- 2026-10-25: Runner fixes after the runs: options given without a value are rejected (the fullscreen run silently used the default focus timeout); spike log names are in UTC (local time repeated an hour at the end of daylight saving time).
- 2026-10-25: Macro 2 fired on every spike 2 message because it is bound to the plain key `2`. Operator rule: no macro or sim control bound to a plain character key on an agent PC. Follow-up: spike 10 also looks for the key bindings, and the self-test (1.3) warns about such bindings if they can be read.
- 2026-10-25: Accepted with the user: normalised session flag names in the snapshot (`flagName` in `webservice/contracts/common.schema.json`). Follow-up for the snapshot builder (1.3): map `SessionFlags` to that vocabulary, with `green` derived (racing and no caution, red or checkered), `caution` for both caution bits, and `caution_waving` in addition while waving.
- 2026-10-25: Accepted: commands whose effect telemetry cannot show (`pit_close`, `pit_open`, `pace_laps_set`, `pace_laps_add`, `clear_all`, `announce`) end at result "sent". Follow-up for 1.3 result reporting: verify the others with the signals listed above.
- Follow-up: the self-test (1.3) should check that iRacing and the agent run at the same elevation.
- 2026-10-25 (spikes 4 to 14): see `docs/spike-results.md`. Values for 1.3: chat line limit 250 (spike 7), pace laps 1 to 20 (spike 9), harmless command `!help` (spike 12), macro texts from `app.ini` `[Autochat Messages]` `AutoChatStr<n>` (spike 10), incidents from the driver list `CurDriverIncidentCount` (spike 4). Live is `ReplayFrameNumEnd` = 1 (spike 6). `!pacelaps` is refused until the pace car's first full caution lap (spike 9, 14). PaceMode shows the restart type only near one lap to green.
- Open: spike 6 per-car telemetry during a replay (re-run `spike6`); whether repeated caution extensions are capped (step 5, failsafe).
- 2026-10-25 (1.3): Contracts are copied into `contracts/` (`scripts/sync-contracts.ps1`) because GitHub CI only has this repository; a test fails when the copy drifts from `../webservice/contracts`. Every message type is validated against its schema with JsonSchema.Net (test only).
- 2026-10-25 (1.3): Event data shapes accepted and added to `webservice/contracts` (42a8245): flag_change `{flags, previous, sessionNum, sessionTimeSec}`, session_change `{leagueId, seasonId, sessionId, subSessionId, num, type}`.
- 2026-10-25 (1.3): Decisions: a repeated command id is never executed again (its earlier result is reported again); yellow during a caution is not thrown again and reports verified; announcements may not start with `!` or `/`; a ready but inactive agent answers commands as failed, a sleeping one stays silent; only the executor executes; verification runs beside the queue and ends at "sent" when no effect shows in time.
- Follow-up (step 3): replace the stub server with the real link; resync uses `EventBuffer.After(lastAcked)`.
- 2026-10-25 (1.3, spike 15): the chat box check costs about 300 ms after each typed line (the line itself is not delayed) and uses the clipboard briefly; clipboard text is restored, images and files are not. The self-test's `!help` is checked the same way.
