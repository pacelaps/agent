# Phase 0 spike results

The checks in iRacing that the agent's design rests on. The raw logs and recordings each result names stay with the
developer (they hold session data); the results below are what they showed.

## How to run

Build with `dotnet build` in the `agent` folder. Then run, in a normal (not administrator) terminal:

```
dotnet run --project src/PaceLaps.Agent.Console -- probe
dotnet run --project src/PaceLaps.Agent.Console -- spike1 --slot <1-15>
dotnet run --project src/PaceLaps.Agent.Console -- spike2 --mode borderless
dotnet run --project src/PaceLaps.Agent.Console -- spike2 --mode fullscreen --focus-timeout 5000
dotnet run --project src/PaceLaps.Agent.Console -- spike3 --car <num> --car2 <num>
dotnet run --project src/PaceLaps.Agent.Console -- spike4
dotnet run --project src/PaceLaps.Agent.Console -- spike5
dotnet run --project src/PaceLaps.Agent.Console -- spike6
dotnet run --project src/PaceLaps.Agent.Console -- spike7
dotnet run --project src/PaceLaps.Agent.Console -- spike8
dotnet run --project src/PaceLaps.Agent.Console -- spike9
dotnet run --project src/PaceLaps.Agent.Console -- spike10 --marker <text>
dotnet run --project src/PaceLaps.Agent.Console -- spike11
dotnet run --project src/PaceLaps.Agent.Console -- spike12
dotnet run --project src/PaceLaps.Agent.Console -- spike13
dotnet run --project src/PaceLaps.Agent.Console -- spike14 --car <your car> --auto
dotnet run --project src/PaceLaps.Agent.Console -- spike15
dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part monitor
dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part send
dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part soak
dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part driving
```

Operator steps: `docs/spike-procedures.md`. Logs: `docs/spike-logs/spike-<n>-<utc-date-time>Z-<runid>.jsonl`. Runner code: `src/PaceLaps.Agent.Console/Spikes/`. Guided spikes (3, 9, 12, 13, 14) read their steps from `src/PaceLaps.Agent.Console/Spikes/spike<n>-steps.json`. Spike 5 also writes a recording to `docs/recordings/`.

## Entries

One entry per spike. Result is **pass**, **fail** or **partial**. Pass criteria are proposals from the spec and can be adjusted (note the change). Spikes 1 to 3 run first.

Record for every run: date, iRacing build/version, agent build (git commit), window mode, and where logs are stored.

## Summary

| # | Question | Result | Date | Decision |
| --- | --- | --- | --- | --- |
| 1 | Do SDK macros run admin commands without iRacing being focused? | pass | 2026-10-25 | Keep the macro channel (optional per agent) |
| 2 | How reliable is typed chat? | pass | 2026-10-25 | Typed channel holds (50/50, median 300 ms). Operators must not bind macros or controls to plain character keys |
| 3 | Do the admin commands behave as documented in an AI hosted session? | pass | 2026-10-25 | All commands work; restart type must be set before one lap to green |
| 4 | How quickly do incident counts update for all drivers? | pass | 2026-10-25 | Use the driver list's incident count (about 0.6 s); results positions did not update |
| 5 | Which per-car fields update live? | pass | 2026-10-25 | All fields present; lap distance updates every frame; recording kept as test data |
| 6 | Can the replay jump to a session time while the sim is live? | pass | 2026-10-25 | Keep the replay channel; open: does per-car telemetry follow a replay? |
| 7 | What is the maximum chat message length? | pass | 2026-10-25 | Chat line limit about 254 characters; agent limit 250 |
| 8 | Can the agent read the operator's name and detect session admin? | pass | 2026-10-25 | Name readable; DriverIsAdmin true as host, false without admin rights |
| 9 | How does a caution extend? | pass | 2026-10-25 | At one to go, !pacelaps +n, !pacelaps n and !yellow all extend; !pacelaps only after the first caution lap; cap 20 |
| 10 | Can the macro configuration be read? | pass | 2026-10-25 | Macro texts in app.ini [Autochat Messages]; key bindings binary (controls.cfg), not read |
| 11 | Are the session identity fields available? | pass | 2026-11-09 | League id 15457 read in a league session, without admin rights |
| 12 | Which harmless command can verify the typed channel? | pass (sender's view) | 2026-10-25 | !help: reply to the sender, no race effect |
| 13 | Can a caution be thrown without the AI pace car? | fail | 2026-10-25 | No session option to disable the pace car: fallback applies |
| 14 | Do Spotter and Crew Chief react to iRacing's flags? | pass | 2026-10-25 | Crew Chief and iRacing's built-in spotter announced caution, green and black flags |
| 15 | Why does the Enter sometimes not send a typed line, and can the agent read the chat box? (added) | partial | 2026-10-25 | Not reproduced (34/34 sent); the chat readback works, so a left-over line can be detected |
| 16 | Can the agent send a chat line without bringing iRacing to the front? (added) | pass | 2026-10-31 | Yes: SDK begin-chat, posted WM_CHAR, posted Enter key, SDK cancel. Out of the car 50/50 lines and 6/6 commands; in the car 24/25 (one miss with a wheel button press); no focus change. Proposed for the typed channel (spec-feedback 37) |

## Spike 1: Do SDK macros run admin commands without iRacing being focused?

- **Test:** Set a macro slot to a harmless line (an /rc message to admins). Focus another window and trigger the slot through the SDK. Repeat 20 times.
- **Pass criteria:** The message appears in iRacing chat every time while iRacing is not focused
- **If it fails:** Drop the macro channel. Everything uses the typed channel

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 00:32 UTC / 2026.10.01.01 / 3e6e0ec
- **Setup:** Hosted AI session, practice session, 21 cars, league id 0 (not a league session). Operator in the session as a driver, not spectating; `DriverIsAdmin` = 1. Borderless window over three 2560x1440 monitors (7680x1440). Neither iRacing nor the runner elevated. Keyboard layout 0407 (German).
- **Notes:**
  - Macro slot 14 = `/rc spike1 macro test$`. The control trigger with iRacing in front worked.
  - 20 of 20 triggers were sent with iRacing not in front, and the operator saw 20 of 20 messages. iRacing never came to the front, and the chat box was never left open.
  - The front window was the browser for trigger 1 and Windows Terminal for triggers 2 to 20 (focus moved to the terminal within 500 ms of trigger 1; cause unknown, probably a click). This does not change the result: the test condition is "iRacing not focused".
  - Every `/rc` message appeared twice, once as `me@ADMIN` and once as `me@PRIVATE`: `/rc` goes to the admins, and the sender also gets a private copy.
  - The SDK macro number is zero-based: slot 14 in the sim's options is SDK macro 13 (`SimConnection.TriggerMacro` converts).
- **Logs:** `docs/spike-logs/spike-1-20261025-023224-a987.jsonl` (file name in local time, CEST)
- **Consequence for the spec or the schemas:** None. The macro channel stays as specified: optional, slots 1 to 15, text ending in `$`. Spike 10 (reading the macro configuration) decides how a slot is verified.

## Spike 2: How reliable is typed chat?

- **Test:** Send 50 harmless /rc messages through the typed channel with the browser focused, in borderless windowed and in fullscreen. Record success, time per command and whether focus returns.
- **Pass criteria:** At least 49 of 50 succeed in borderless windowed, the median is under 500 ms, and focus is restored each time
- **If it fails:** Require borderless windowed, or show the command for the operator to type

- **Result:** pass. The unintended macro trigger is explained by the operator's key binding (see below) and becomes an operator rule.
- **Date / iRacing build / agent build:** 2026-10-25 00:36 and 00:44 UTC / 2026.10.01.01 / 3e6e0ec
- **Setup:** As spike 1 (same session). The browser (Firefox) was in front before every message. Text: `/rc s2 bw NN/50 <run id>` (20 characters).
- **Borderless windowed:**
  - Arrived: 50 of 50, none garbled, and focus was restored 50 of 50 times. The chat box was never left open.
  - Total time: median 300 ms, p95 316 ms, max 332 ms.
  - Phases (median): focus 16 ms, chat open 158 ms (150 ms configured wait), typing plus Enter 124 ms (100 ms configured wait after Enter), restore 3 ms.
  - Focus method: plain `SetForegroundWindow` never succeeded on its own. The unassigned-key press then `SetForegroundWindow` worked 3 times, and `AttachThreadInput` 47 times.
- **"Fullscreen" run:** 50 of 50 arrived, median 298 ms, focus restored 50 of 50. Recorded only; it is not a pass criterion. Two caveats:
  - The window geometry and styles were identical to the borderless run (7680x1440, popup, no caption). The operator runs triple screens with an edited iRacing config file, which probably makes "fullscreen" behave like borderless. So **exclusive fullscreen is untested** on this setup.
  - The run was started with `--focus-timeout` but no value, so the default 1000 ms was used. No send came near that. The parser now rejects an option given without a value.
- **Macro side effect (explained):** operator note on the borderless run: "All messages triggered my macro number 2". Macro 2 is bound to the plain key `2`, and every message contained a `2` (`s2`). So iRacing acts on a bound key even while the chat box is open and the key is typed into it. Whether manual typing does the same was not checked separately; the agent's scancodes look like real key presses to the sim, so the same is expected.
- **Logs:** `docs/spike-logs/spike-2-20261025-023652-0669.jsonl` (borderless), `docs/spike-logs/spike-2-20261025-024427-c10e.jsonl` (fullscreen)
- **Consequence for the spec or the schemas:**
  - The typed channel holds. With about 300 ms, of which 250 ms are configured waits, the spec's 500 ms target has margin. Lowering the waits is a later tuning step, not needed now.
  - `AttachThreadInput` is the method that works in practice: keep the fallback chain.
  - **Operator rule:** on an agent PC, no chat macro or sim control may be bound to a plain character key (letters, digits, `#`, `+`, `!`, space). Car numbers are digits, so such a binding fires on almost every command. Use modifier combinations or wheel buttons instead. The self-test should check the bindings if they can be read; spike 10 looks for the macro configuration, and it should also look for the key bindings.
  - Exclusive fullscreen stays untested. The spec already recommends borderless windowed; the agent's setup guide states it as the supported mode.

## Spike 3: Do the admin commands behave as documented in an AI hosted session?

- **Test:** With the league's AI-enabled cars, run !yellow, !restart single and double, !pacelaps (absolute and relative), !waveby, !pitclose, !pitopen, !black, !clear and !eol. Record the effect of each.
- **Pass criteria:** Each command has the documented effect, and the AI pace car picks up the leader
- **If it fails:** Remove unsupported commands from the catalogue and repeat the test in a hosted session with other drivers

- **Result:** pass. Every command has the documented effect, and the AI pace car picked up the leader. In this run `!restart single` was sent after "one lap to green" and did not apply to that restart. The operator later tested it sent in time, and it worked (operator's own test, no runner log).
- **Date / iRacing build / agent build:** 2026-10-25 01:44 to 02:20 UTC / 2026.10.01.01 / 3e6e0ec
- **Setup:** Hosted AI session, race session (lone qualify, then race), 26 cars in several classes, league id 0. Operator as a driver, admin. Target car #3 (car index 7), second car #4. All 15 commands were sent through the typed channel (median about 290 ms, all succeeded, focus restored every time).
- **Confirmed syntax** (car numbers with `#`): `!black #3 D` (drive-through), `!black #3 10` (stop and go), `!clear #3`, `!waveby #4`, `!eol #3`, `!pacelaps 4`, `!pacelaps +2`, `!restart double`, `!restart single`, `!pitclose`, `!pitopen`, `!yellow`, `!yellow <message>`, `!clearall`.
- **`!help` lists:** admin, advance, black, chat, clear, clearall, dq, eol, help, ?, nadmin, nchat, /all, /rc, remove, showdqs, waveby, yellow, pacelaps, restart, trackstate, mods, gridset, gridstart. `!pitclose` and `!pitopen` are **not** listed, but both work.
- **Per command** (operator verdict, then what telemetry showed):

  | Command | Operator | Telemetry |
  | --- | --- | --- |
  | `!pitclose` / `!pitopen` | pit lane closed / opened | `PitsOpen` stayed `True` throughout: **not verifiable** from this variable |
  | `!black #3 D` | black flag, drive-through served | `CarIdxSessionFlags[7]` black bit within 0.11 s (next sample); cleared on leaving pit road after serving (+65 s) |
  | `!black #3 10` | black flag | black bit within 0.11 s |
  | `!clear #3` | black flag cleared | black bit cleared within 0.11 s |
  | `!yellow` | full course yellow, pace car picked up the leader | `cautionWaving` within 0.11 s, then `caution`; `PaceMode` went to single file restart (session default); pace car within 5 % of a lap ahead of the leader after 47 s |
  | `!pacelaps 4`, `!pacelaps +2` | set to 4, extended by 2; the caution lasted 6 laps | no variable shows pace laps (`SessionLapsRemainEx` 32767 throughout): **not verifiable** |
  | `!waveby #4` | passed the pace car, went to the end of the line | `CarIdxPaceFlags` `wavedAround` within 0.11 s; cleared at the restart |
  | `!eol #3` | moved to the end of the line | `CarIdxPaceFlags` `endOfLine` immediately; cleared when the next caution began |
  | `!restart double` | field lined up double file | `PaceMode` did **not** change within the 30 s watch, but was `doubleFileRestart` 6.8 min later: it changes late |
  | `!restart single` | sent after one lap to green; chat confirmed it, but the restart was still double file | `PaceMode` stayed double. The next caution (`!yellow <message>`) started single file, which is the session default anyway |
  | `!yellow <message>` | caution; the message appeared as race control (pink). Re-tested by the operator with other drivers: shown to all drivers | caution within 0.21 s |
  | `!clearall` | nothing to clear | none |

- **Other observations:**
  - Under green, `SessionFlags` mostly holds only `startHidden`. The `green` bit is set for a few seconds after the green flag, then drops. "Green" has to be derived (no caution, red or checkered), not read from the bit.
  - `CarIdxSessionFlags` is readable for every car, not only the operator's car: black flags on AI cars were visible.
  - The operator was a driver, not a spectator; admin commands worked.
- **Logs:** `docs/spike-logs/spike-3-20261025-024428-34a4.jsonl` (file name in local time: CET, after the clocks changed)
- **Restart single:** confirmed with a log on 2026-10-25 03:41 UTC (`docs/spike-logs/spike-3-20261025-034123Z-fad3.jsonl`, `spike3-restart-recheck.json`). `!restart double`, then `!restart single`, both sent early in the caution, each took effect. PaceMode again did not show the change within the 20 s watch: it reflects the restart type only near one lap to green (seen in spike 14), so restart commands normally end at "sent".
- **Consequence for the spec or the schemas:** Changes to the specification:
  - No command has to be removed from the catalogue.
  - `pit_close`, `pit_open` and `pace_laps_*` can only report "sent", not "verified", unless the session info shows them.
  - The restart type has to be set before one lap to green.
  - "Green" is derived from the flags, not read from the green bit.
  - The `!yellow` message is shown to all drivers (operator re-test with other drivers, 2026-10-25).

## Spike 4: How quickly do incident counts update for all drivers?

- **Test:** With two cars, cause a 1x and a 4x incident. Measure the time until the count changes in the session info, as read by a spectator.
- **Pass criteria:** The change appears within 2 seconds
- **If it fails:** Show incident entries as delayed, or limit the log to manual entries

- **Result:** pass, using the driver list's incident count. (The runner printed "fail", because it judged the results positions, which never changed; see below.)
- **Date / iRacing build / agent build:** 2026-10-25 12:37 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Setup:** Hosted AI race (session 0 Race, 21 cars). The operator drove car #218.
- **Notes:**
  - Three incidents: off track (0 to 1), contact with #1 (1 to 5), off track (5 to 6).
  - Telemetry (`PlayerCarMyIncidentCount`) changed at once. The session info driver list (`CurDriverIncidentCount`, and `DriverIncidentCount` for the own car) showed the new total 0.65 s, 0.57 s and 0.63 s later.
  - The AI car #1 got its 4x in the driver list in the same session info update as the operator's own change, so the driver list carries other cars' counts too.
  - `ResultsPositions[].Incidents` for the current session did not change at all during the run. It is not a live source.
  - "As read by a spectator": read on the driving operator's PC, from the session info, which every participant receives. A separate spectator PC was not tested.
- **Logs:** `docs/spike-logs/spike-4-20261025-123732Z-181f.jsonl`
- **Consequence:** the agent reads incidents from the driver list (`CurDriverIncidentCount`), not from the results positions. The proof of concept is changed accordingly.

## Spike 5: Which per-car fields update live?

- **Test:** While spectating a full AI field, dump lap distance, class, pit road, surface, position, laps completed and the pace car flag for every car, and compare with the screen.
- **Pass criteria:** All fields are present and update at least once per second for every car
- **If it fails:** Derive missing fields from others, or drop the features that need them

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 03:58 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Setup:** Hosted AI race (session 1, 26 cars, several classes), 5 minutes, every frame captured (20,140 frames, 60 Hz).
- **Notes:**
  - All per-car fields are broadcast: `CarIdxLapDistPct`, `CarIdxLap`, `CarIdxLapCompleted`, `CarIdxOnPitRoad`, `CarIdxTrackSurface`, `CarIdxPosition`, `CarIdxClassPosition`. Class and the pace car flag come from the session info.
  - Lap distance changed every frame for every moving car: the longest gap on track was 0.021 s. Two car indices never moved: index 0 (the operator's car, not in the world) and index 2 (a parked car).
  - Over the 5 minutes, laps changed for 24 cars, positions for 7, and pit road and surface for 2. These are discrete fields that change when something happens.
  - Cars not in the world report lap -1, lap distance -1 and position 0. The snapshot builder clamps them to 0 and marks them as not connected.
  - Both comparisons with the sim's standings matched.
  - The 2 Hz recording (`docs/recordings/spike5-20261025-035810Z-fd3a.jsonl`, 2.8 MB) is test data for the web service's sorting view.
- **Logs:** `docs/spike-logs/spike-5-20261025-035810Z-fd3a.jsonl`
- **Consequence:** none for the schema. The provisional snapshot mappings hold: lap = laps completed, connected = car in the world.

## Spike 6: Can the replay jump to a session time while the sim is live?

- **Test:** Jump to session numbers and times through the SDK during a live session, then return to live.
- **Pass criteria:** The jump works and the sim returns to live
- **If it fails:** Drop the replay jump (it is on the later list anyway)

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 12:02 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Setup:** Hosted AI race, session 0, so the previous-session jump was not possible. Four jumps: 60 s back, and three quick jumps.
- **Notes:**
  - Every jump moved the replay (24,800 to 41,300 frames behind live), and `ReplaySearch(ToEnd)` with normal speed returned to live every time (`ReplayFrameNumEnd` back to 1). The operator saw it 4 of 4 times.
  - `IsReplayPlaying` is true while live too. Live is `ReplayFrameNumEnd` = 1, not `IsReplayPlaying` = false.
  - `SessionTime` kept running live during the replay, while `ReplaySessionTime` showed the replay position.
  - **Open:** the runner sampled only the operator's own car (not in the world), so it cannot say whether the per-car arrays (`CarIdxLapDistPct` and others) show the replay or the live race while replaying. If they show the replay, an operator who watches a replay on the agent PC would corrupt the snapshots. A 5-minute re-run that samples all cars answers it.
- **Logs:** `docs/spike-logs/spike-6-20261025-120237Z-bb1c.jsonl`
- **Consequence:** keep the replay channel; it stays on the later list. Re-check the per-car question before snapshots are relied on.
- **Per-car question answered (2026-11-03, the user's observation while building race control's map cards):** with
  iRacing in a replay, paused or playing, the map card kept showing every car's live position. The per-car arrays stay
  live during a local replay, so an agent can replay without corrupting its snapshots. The re-run was not needed;
  `spike6 --car N` (camera on a car) remains for the incident review's live check.
- **Camera on a car (2026-11-03, iRacing 2026.11.02.01, the user's AI race at Lime Rock Park, 7 cars, `spike6 --car 2`):
  pass.** 4 of 4 jumps (180 s back and three quick ones) moved the replay, the camera followed car #2 every time
  (`CamCarIdx` 3, its index) and `ReplaySearch(ToEnd)` returned to live. The run also confirmed the per-car question:
  laps completed kept counting and `SessionTime` kept running during each replay. Log:
  `docs/spike-logs/spike-6-20261103-153624Z-d939.jsonl`.

## Spike 7: What is the maximum chat message length?

- **Test:** Send messages of increasing length through the typed channel and note where the text is cut.
- **Pass criteria:** The limit is known
- **If it fails:** Shorten the announcement templates to fit

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 03:50 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Notes:**
  - With `/rc` and as plain chat alike, messages up to 200 characters arrived whole.
  - Every longer message (255, 256, 300, 400) showed the last mark "250". Counting the dots after it, the operator puts the limit at 254 characters. Text beyond that is dropped.
  - Messages over about 60 characters wrap onto further lines in chat but stay one message.
  - Typing time grows with length: about 150 ms for 60 characters, 400 ms for 400 characters (all keys in one SendInput).
- **Logs:** `docs/spike-logs/spike-7-20261025-035035Z-7206.jsonl`
- **Consequence:** the agent's limit for a whole chat line (command plus free text) is 250 characters, a small margin below 254. Announcement templates need no shortening.

## Spike 8: Can the agent read the operator's name and detect session admin?

- **Test:** Read the local user's name from the driver list, and look for any field that shows admin rights.
- **Pass criteria:** The name is readable, and admin detection is either available or replaced by the self-test result
- **If it fails:** Show the name as unknown, and treat the typed self-test as the admin check

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 12:49 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Notes:**
  - In an official testing session, where the operator is not admin: the name was readable (17 characters; not logged) and `DriverIsAdmin` was false, matching the operator.
  - In every hosted session of spikes 1 to 14 the operator was host, and `DriverIsAdmin` was true.
- **Logs:** `docs/spike-logs/spike-8-20261025-124920Z-04b7.jsonl`
- **Consequence:** the agent reports the operator name in hello and can read admin rights. The self-test can check `DriverIsAdmin` before typing, and the `!help` reply stays the final proof.

## Spike 9: How does a caution extend?

- **Test:** Run a caution, then use !yellow at one lap to go and !pacelaps with +n and with an absolute n. Record how long the caution lasts.
- **Pass criteria:** The caution can be held for the intended extra laps
- **If it fails:** Remove the keep-alive from the default local failsafe procedure

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 04:10 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Notes:**
  - The armed steps were sent the moment `one_lap_to_green` showed. In each case telemetry showed the caution still on without one-to-go within 0.3 s:
    - `!pacelaps +2`: extended by 2 laps;
    - `!yellow` during the caution: extended by one lap;
    - `!pacelaps 3`: extended to 3 laps to green.
  - **`!pacelaps` is refused until the pace car has completed its first full lap of the caution** (spike 9, and again in spike 14). It is accepted afterwards.
  - `!pacelaps 99` gave 20 laps, and `!pacelaps 20` was accepted. The maximum is 20.
  - Still open (operator's question): whether the total of a caution is capped, or whether +2 at every one-to-go keeps it alive indefinitely. The failsafe keep-alive depends on it.
    **Answered 2026-10-28** (full agent test, `docs/full-agent-results.md`): the local failsafe sent `!pacelaps +1` at three one-to-go flags in a row of one caution, and each time the caution went on for another lap. No cap seen within three extensions.
  - The first restart came a few seconds after the 600 s watch ended. The second ended with a green flag 9 s into its watch.
- **Logs:** `docs/spike-logs/spike-9-20261025-041016Z-8dd3.jsonl`
- **Consequence:**
  - The keep-alive stays in the default local failsafe: at one lap to green, `!pacelaps +n`.
  - The pace-lap limit is 20.
  - Pace-lap commands are only sent after the first full caution lap (a server rule).
  - Whether repeated extensions are capped is to check in a longer failsafe test (step 5). Checked 2026-10-28: three in a row worked.

## Spike 10: Can the macro configuration be read?

- **Test:** Find where iRacing stores the macros, parse them read-only, change a macro, restart the sim and parse again.
- **Pass criteria:** The slot texts are read correctly after a restart
- **If it fails:** Use the guided test and the operator's confirmation

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-10-25 12:51 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Notes:**
  - The macro texts are in `Documents\iRacing\app.ini`, section `[Autochat Messages]`, keys `AutoChatStr1` to `AutoChatStr15`. The value is followed by spaces and the comment `; Auto chat message`.
  - Slot 5 showed `spike10markerA$`. After the change and a sim restart it showed `spike10markerB$`, and marker A was gone.
  - The sim writes these settings when it closes: a macro changed in a running sim shows in the file only after the sim exits.
  - Key bindings are in `controls.cfg` (in the documents folder and per profile, for example `profiles\controls\Baseline\controls.cfg`). It is a binary format (header `GFCC`) with entries named `Chat1` to `Chat15`, so the macro bindings are in there, but reading them needs the format reverse-engineered.
- **Logs:** `docs/spike-logs/spike-10-20261025-125135Z-f9f7.jsonl`, `-125415Z-291c.jsonl`, `-125442Z-20ef.jsonl`
- **Consequence:**
  - Self-test step 6 reads the slot texts from `app.ini`; a slot whose file text does not match is not offered.
  - The key-binding rule from spike 2 stays an operator confirmation; reading `controls.cfg` is on the later list.

## Spike 11: Are the session identity fields available?

- **Test:** In a hosted league session, read league id, season id, session id and sub-session id.
- **Pass criteria:** The league id is not zero and matches the league
- **If it fails:** The operator selects the league in the agent

- **Result:** pass
- **Date / iRacing build / agent build:** 2026-11-09 18:16 UTC / 2026.11.02.01 / 0.1.0+eeaf516
- **Notes:**
  - In a league session (a practice of another league, the user joined as a driver without admin rights): league id
    15457, season id 0, session id 327492457, sub-session id 89997704. The summary's league page id is the league id.
  - Earlier (2026-10-25, build 2026.10.01.01): in an official testing session league id 0, season id 6463; in the
    hosted sessions of the other spikes league id 0 and sub-session id 0.
  - The agent reads the league id without admin rights.
- **Logs:** `docs/spike-logs/spike-11-20261109-181633Z-e355.jsonl`, `docs/spike-logs/spike-11-20261025-125005Z-bc81.jsonl`
- **Consequence:** none. The agent takes the league from the session; the fallback (the operator selects the league)
  stays for hosted sessions outside a league.

## Spike 12: Which harmless command can verify the typed channel?

- **Test:** Try !help and an /rc message, and note what is displayed and to whom.
- **Pass criteria:** One command has no race effect and gives an observable result
- **If it fails:** The self-test marks the typed channel as unverified

- **Result:** pass for the sender's view. Who else sees the reply is untested (no second person).
- **Date / iRacing build / agent build:** 2026-10-25 03:49 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Notes:**
  - `!help` and `!help yellow`: a reply appeared every time; flags, penalties and pit state were unchanged.
  - `/rc spike12 race control test` appeared as `@ADMIN` and `@PRIVATE` (as in spike 1).
- **Logs:** `docs/spike-logs/spike-12-20261025-034907Z-c83f.jsonl`
- **Consequence:** `!help` is the self-test's harmless command, and the operator confirms the reply. This needs admin rights, so it also proves the operator can run admin commands.

## Spike 13: Can a caution be thrown without the AI pace car?

- **Test:** Look in the session options for a way to disable the pace car, and test !yellow with it.
- **Pass criteria:** A caution works with no AI pace car
- **If it fails:** Keep the custom safety car as a green flag procedure, and document that Spotter and Crew Chief will not announce it

- **Result:** fail
- **Date / iRacing build / agent build:** 2026-10-25 12:45 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Notes:** The operator found no option in the hosted session settings to disable the AI pace car, so the steps were skipped. (`PaceCarIdx` was 64 as usual.)
- **Logs:** `docs/spike-logs/spike-13-20261025-124506Z-857a.jsonl`
- **Consequence (fallback applied):** the custom safety car stays a green-flag procedure, and Spotter and Crew Chief will not announce it. The spec already describes this; its "unless spike 13 shows otherwise" goes.

## Spike 14: Do Spotter and Crew Chief react to iRacing's flags?

- **Test:** Run both during an AI session, use !yellow and black flags, and note what they announce.
- **Pass criteria:** Both announce the caution and the black flags
- **If it fails:** Document the limitation

- **Result:** pass. Crew Chief and iRacing's built-in spotter both announced everything. (The runner recorded "partial" because the operator added the spotter observation afterwards, on 2026-10-25.)
- **Date / iRacing build / agent build:** 2026-10-25 12:23 UTC / 2026.10.01.01 / spikes-4-14 (3e47989)
- **Setup:** Operator driving car #218 with Crew Chief running; `--auto` mode.
- **Notes:**
  - Crew Chief announced the caution, the green flag at the restart, and the black flags (drive-through, and stop and go). iRacing's built-in spotter announced them correctly too (operator, after the run).
  - Telemetry showed every flag within 0.4 s of the command.
  - `!pacelaps 2` was sent before the pace car's first full lap and had no effect (see spike 9).
  - With the operator driving, `SessionFlags` also carries the operator's own car flags (blue, black). They are per-car and are not part of the snapshot's session flags.
- **Logs:** `docs/spike-logs/spike-14-20261025-122351Z-6964.jsonl`
- **Consequence:** native cautions and black flags reach drivers through Crew Chief and iRacing's spotter. A separate spotter app (if a league member uses one) is not tested.

## Spike 15: Why does the Enter sometimes not send a typed line, and can the agent read the chat box? (added 2026-10-25)

- **Background:** in the proof-of-concept acceptance run, `!eol #7` stayed in the chat box twice: the keystrokes and the Enter were accepted by Windows, focus came back, but iRacing did not send the line. #7 was the only target whose number starts other car numbers (#77, #777). No suggestion list appears in chat.
- **Test:**
  - Read back an unsent line and an empty chat box through the clipboard.
  - Send `!black #<car> D` and `!clear #<car>` on a suspect and a control car in four forms (plain, 100 ms and 300 ms pause before Enter, trailing space), with a telemetry check and a readback after each.
  - Optionally, `!eol` under caution.
- **Pass criteria (proposed):** the cause is found, or a form that always sends is found; and the readback works, so the agent can detect and clear a line left in chat.
- **If it fails:** keep a safety cancel after every typed line (clears chat without knowing its content), and rely on telemetry verification to report "not seen".

- **Result:** partial. The readback works; the lost Enter did not reproduce, so its cause is still unknown.
- **Date / iRacing build / agent build:** 2026-10-25 14:25 UTC / 2026.10.01.01 / spike-15 (20c3e6c)
- **Setup:** Hosted AI race, 21 cars including #7, #77 and #777. Suspect car #7 (starts #77 and #777); control car #3 (starts no other number).
- **Notes:**
  - **Phase A (readback):**
    - An unsent line (`spike15 readback test`, typed without Enter) was read back exactly through begin-chat, Ctrl+A and Ctrl+C. Afterwards the chat box was closed and empty.
    - An empty chat box read as "nothing copied".
    - So begin-chat keeps text already in the box, and the chat box supports select-all and copy.
  - **Phase B (green flag):** 32 of 32 lines were sent and took effect within 0.06 to 0.37 s, with no left-over text. That covers `!black #<car> D` and `!clear #<car>` on #7 and #3, in four forms each (plain, 100 ms or 300 ms before Enter, trailing space), twice. The readback after each sent line did no harm.
  - **Phase C (caution):** plain `!eol #7` and `!eol #3` both took effect. The operator confirmed both cars got the end-of-line penalty.
  - The prefix theory (#7 starts #77) does not hold: plain `#7` lines worked every time. The two failures in the acceptance run remain unexplained. They happened during a caution about 30 s before one lap to green; the cause may be timing in the sim.
- **Logs:** `docs/spike-logs/spike-15-20261025-142530Z-5a4b.jsonl`
- **Consequence:** the lost Enter is rare and its cause unknown, so the typed channel has to detect it rather than avoid it. After every typed line, the agent reads the chat box (it is still in front):
  - empty: the line was sent;
  - still holding exactly the line: press Enter once more (the line was never sent, so this is not a repeat), then check again;
  - anything else, or still not sent: clear it and report the failure with the text found.
  
  No pause before Enter or trailing space is needed.

## Spike 16: Can the agent send a chat line without bringing iRacing to the front? (added 2026-10-28)

- **Background:**
  - The typed channel brings iRacing to the front for every line, which the operator finds clunky.
  - The operator saw that the other tool sends commands without iRacing coming to the front.
  - Published tools that type into chat (iRaceDeck, Track Thirty Two's control decks) focus iRacing first. iRaceDeck pastes with Ctrl+V.
  - The SDK can open, cancel and trigger macros in chat without focus (spike 1), but cannot send text.
- **Test:**
  - **Part monitor:** while the other tool sends messages, watch passively:
    - the front window;
    - injected keys and mouse (low-level hooks);
    - clipboard changes (owner only);
    - the SDK's broadcast messages (a hidden top-level window receives them all);
    - writes to `app.ini`.
  - **Part send:** numbered `/rc` lines with another window in front:
    - A: window messages (WM_CHAR, Enter as WM_KEYDOWN);
    - B: window messages (WM_CHAR, Enter as WM_CHAR);
    - C: WM_PASTE;
    - D: focus and paste, for comparison.

    The chat box is read back after each line.
- **Pass criteria (proposed):** one way without a focus switch delivers every line, leaves nothing in the chat box, and has no side effects in the sim.
- **If it fails:**
  - Keep the typed channel.
  - Consider paste instead of typing: a shorter time in front, and no character keys that could fire the operator's bindings.
  - Recommend a dedicated race control PC.
- **Runner:** `src/PaceLaps.Agent.Console/Spikes/Spike16Background.cs`, with `InputMonitor`, `WindowMessageTyping` and `SdkBroadcast` in `src/PaceLaps.Agent.IRacing/Win32/`.
- **Result:** pass. Variant A sends a chat line with no focus change. The other tool uses the SDK in the same pattern.
- **Date / iRacing build / agent build:** 2026-10-31 11:38 to 11:44 UTC / 2026.10.01.01 / 0.1.0+344f880.
- **Setup:**
  - Hosted AI practice, 10 cars, the operator host and admin (`DriverIsAdmin`), not driving.
  - Borderless window (class `SimWinClass`, no child windows), keyboard layout 0407 (German).
  - Neither iRacing nor the runner elevated.
- **Part monitor** (the other tool sending its messages and commands):
  - **No focus change:** the front window was only the other tool, then the terminal. iRacing never came forward.
  - **No injected keys or mouse input, no clipboard change, no `app.ini` write.** The only keys were the operator's own typing into the other tool.
  - **The SDK in pairs:** each message showed `ChatCommand BeginChat`, then `ChatCommand Cancel` 8 to 17 ms later (11 pairs). Some messages showed two pairs about 300 ms apart.
  - So the other tool opens the chat box through the SDK and puts the line in by a route the passive monitor cannot see: window messages to the sim's window. Then it closes the box with the SDK's cancel.
  - It also sent `TelemCommand 1` (start telemetry recording) every 13 to 26 s. Not relevant here.
  - The run ended without its closing questions, so the operator's per-message answers are not in the log.
- **Part send** (another window in front, three numbered lines per variant):

  | Variant | Seen in chat | Left in the chat box | Focus change |
  | --- | --- | --- | --- |
  | A: begin chat (SDK), WM_CHAR per character, Enter as WM_KEYDOWN/UP | 3 of 3 | 0 | none |
  | B: as A, Enter as WM_CHAR | 0 of 3 | 3: the text arrived, the Enter was ignored | none |
  | C: begin chat (SDK), WM_PASTE, Enter as WM_KEYDOWN/UP | 0 of 3 | 0: the paste was ignored | none |
  | D: focus, begin chat, Ctrl+V, Enter, back | 3 of 3 | 0 | on purpose; iRacing in front about 320 ms |

  - Every post was accepted by Windows (no failed posts).
  - Variant A took 214 to 227 ms, almost all of it the runner's own waits (150 ms after begin-chat, 50 ms before Enter).
  - The monitor saw no focus change and no injected key between begin-chat and the readback. The only switches and keys in the log belong to the readback (iRacing forward, Ctrl+A, Ctrl+C) and to variant D.
  - No side effects in the sim, and the chat box was never left open. The lines contain digits (`01` to `12`). In spike 2, typed digits fired a macro bound to the plain key 2, but no binding fired here.
- **Logs:** `docs/spike-logs/spike-16-20261031-113857Z-0551.jsonl` (monitor), `docs/spike-logs/spike-16-20261031-114209Z-ea5b.jsonl` (send).
- **Consequence (proposed, for the user's decision):**
  - The typed channel can drop the focus switch:
    1. begin chat through the SDK;
    2. WM_CHAR per character and Enter as WM_KEYDOWN/UP, posted to the sim window;
    3. cancel chat through the SDK right after, as the other tool does, so the box is never left open.
  - **Expected side benefits:**
    - The text is sent as Unicode characters, so the keyboard layout no longer matters.
    - No scancodes are pressed, so the operator's bindings on plain keys probably no longer fire. Seen once here; to confirm.
  - **Lost:** the chat box readback (spike 15) needs the focus. A line whose Enter is lost would be cancelled, not sent.
    - Commands verifiable in telemetry still report "not seen".
    - The others (announce, pit lane) would report "sent" without proof. The proposal: read back only when a verifiable command was not seen.
  - **To measure before it replaces the focus switch:**
    - 50 lines, as in spike 2;
    - without the 150 ms wait (the other tool needs about 10 ms);
    - real admin commands (`!black`, `!clear`, `!yellow` with a message);
    - with the operator driving (iRacing in front anyway);
    - with a binding on a plain key.
- **Part soak** (built 2026-10-31, `--part soak`): those measurements in one run. The SDK's begin chat and cancel chat are posted messages (IRSDKSharper imports only `PostMessage`), so posted text and Enter reach the sim in order between them. The first half has no waits, the second has short ones as a margin.
  - **Result: partial.** Out of the car it works without fault. While the operator drives, nothing arrived.
  - **When:** 2026-10-31, 11:56 to 12:12 UTC, agent build 0.1.0+1301d17.
  - **Setup:** hosted AI race, 10 cars; the operator host and admin; neither iRacing nor the runner elevated.
  - **Phase 1** (garage, another window in front): **50 of 50 lines arrived**, 25 with no waits (begin chat, text, Enter and cancel chat posted back to back) and 25 with waits.
    - Nothing was left in the chat box, and the test binding (`/rc BINDING FIRED` on the plain key 5) never fired.
    - The monitor counted 0 focus changes, 0 injected keys and 0 clipboard changes.
  - **Phase 2** (garage, another window in front, no waits):
    - `!black #2 D` and `!clear #2` three times each: **6 of 6 seen in telemetry**, 0.30 to 0.42 s after the send.
    - `/rc s16 Zeichen: äöüß é # ! ? , . : ; ( ) - + = @ 60` arrived **exactly**, on the German layout.
    - `!yellow Spike 16 test caution, please ignore`: the caution showed after 0.37 s, and iRacing showed the message.
    - Each line took 10 to 16 ms from begin chat to cancel chat. The typed channel needs about 650 ms with its focus switch (production log, 2026-10-28).
  - **Phase 3** (the operator in the car, on track, no waits): **0 of 10 lines arrived.**
    - Lines 51 to 57 went out while the terminal was still in front (the operator clicked into iRacing at 12:10:45), lines 58 to 60 with iRacing in front.
    - The posts were accepted, and begin chat and cancel chat went out for every line.
    - Nothing disturbed driving.
    - So the difference to phases 1 and 2 is being in the car, not which window is in front.
    - Only the no-wait form was tried in the car.
  - **Log:** `docs/spike-logs/spike-16-20261031-115633Z-0473.jsonl`.
  - **Open:**
    - Does the in-car chat need time to open, so that the waits (or longer ones) make posted text arrive?
    - Or does iRacing ignore posted characters while the player drives?
    - Does the other tool's message arrive while its operator drives?
    - The typed channel with its focus switch did work while driving (spike 14, `--auto`).
- **Part driving** (2026-10-31, 12:36 to 12:45 UTC, agent build 0.1.0+189c4dd; hosted AI race; the operator in the car throughout).
  - **Result: pass.** In the car, posted lines arrived 24 of 25. The one miss coincided with a wheel button press at the pit exit.
  - **Phase A** (the other tool, the operator's car in the pit stall):
    - its `/rc` message arrived;
    - the monitor timed its begin chat to cancel chat at **13 ms**;
    - the operator saw the chat box "for a blink of an eye";
    - a public message was not tried (not found in the other tool).
  - **Phase B** (in the car, parked in the pit stall per telemetry, the browser in front): **15 of 15** arrived. That covers all five forms three times each:
    - `d0`: no waits, 13 to 15 ms;
    - `d1`: 50 and 100 ms, about 170 ms;
    - `d2`: 150 and 300 ms, about 460 ms;
    - `d3`: 300 and 500 ms, about 820 ms;
    - `dS`: sent instead of posted, about 610 ms.
  - **Phase C** (iRacing in front, driving): **9 of 10** arrived.
    - The miss was #16 (`d0`), sent as the car left the pit lane (`OnPitRoad` true, out of the stall). The operator pressed the pit limiter button on the wheel at that moment.
    - The monitor saw no key or focus change then: wheel buttons are not keyboard input.
    - Every other form, and `d0` again on track (#21), arrived. Nothing disturbed driving.
  - **The soak's 0 of 10 in the car stays unexplained.** It was the same form (`d0`), and the posts were accepted then too.
    - Here the in-car case works, so being in the car is not the cause.
    - Something in that session's state is the likely reason: it was 14 minutes into the race, after the test caution. The per-line telemetry that would show it was not logged then; it is now.
  - **Log:** `docs/spike-logs/spike-16-20261031-123627Z-6411.jsonl`.
- **Consequence** (spec-feedback 37, decided by the user 2026-10-31; built 2026-10-31: `TypedChatSender` posted way, `PostedChatInput`, the in-queue check and repeat in `CommandExecutor`, setting `typedWithoutFocus`):
  - The typed channel sends without the focus switch:
    1. SDK begin chat;
    2. the text as posted WM_CHAR;
    3. Enter as posted WM_KEYDOWN/UP;
    4. a 100 ms margin;
    5. SDK cancel chat.

    That is form `d1` (about 170 ms; the chat box shows for a few frames), as a margin over the other tool's 13 ms.
  - The keyboard layout no longer matters.
  - Because the text cannot be read back without the focus, three things change:
    - A command verifiable in telemetry that is not seen within its time is typed once more with the old focus switch and readback (spike 15), and then reported.
    - The others report "sent (cannot be verified)", as macros do.
    - The self-test's typed check keeps the focus switch: the operator is at the PC then.
  - The operator rule "spectating, not driving" can go: the new way works while driving.
  - The no-plain-key-bindings rule is kept until a run with a binding confirms it can go. In the soak the test binding never fired, but whether it was set up is not recorded.
- **Checked in the sim** (2026-10-31, tray app 0.7.0-rc.1+fae9128, production, hosted AI race): pass.
  - Ten lines the posted way in 159 to 172 ms: announcements, pit close and open, a caution with a message, pace laps, two drive-throughs and their revocations, the second set from the phone while driving. The self-test's `!help` went the focus way (626 ms).
  - Every verifiable command was verified at once (60 to 250 ms after its line), so none was repeated with the focus switch.
  - The operator saw iRacing come to the front only for the self-test, and driving was not disturbed.
  - **Log:** `docs/poc-logs/posted-chat-check-20261031.txt` (the tray app's log from its start to Sleep).

## Lap times check: what iRacing's timing data looks like (added 2026-11-02)

For race control's lap times card. Runner: console
`lap-times-record`; check: `python scripts/lap-times-check.py FILE`.

- **Checked in the sim** (2026-11-02, iRacing 2026.10.01.01, the user's AI race at Lime Rock Park Grand Prix, six AI
  MX-5s, 6 laps; a drive-through for #1 and a 10 s stop and go for #46 given from chat): pass.
  - **Sector starts:** `SplitTimeInfo` lists 4 sectors (0, 0.2307, 0.4786, 0.6278 of the lap).
  - **Measured sectors add up to iRacing's lap time:** on 24 clean laps the sum of the sector times from the
    interpolated crossings differs from `CarIdxLastLapTime` by at most 0.001 s.
  - **iRacing's last lap time arrives late:** 0.11 to 1.02 s after the car crosses the line (median 0.46 s).
  - **Laps without a time from iRacing (`CarIdxLastLapTime` -1):** #1's drive-through in-lap, #46's lap after its
    penalty was given (still on track, black flag out) and its stop-and-go lap. #1's out-lap after the drive-through
    has a time; there the measured sum is 0.033 s longer (the pit lane).
  - `CarIdxBestLapTime` and `CarIdxBestLapNum` follow the fastest timed lap.
  - Not covered: an off-track lap (nobody drove; the AI stayed on track).
  - **Consequence:** sector times come from the agent's measured crossings (as planned); Last and Best from iRacing's
    values, paired with the lap on the server up to a few seconds after the line; a lap iRacing does not time shows
    the measured time, marked, and is never a best.
  - **Logs:** `docs/spike-logs/lap-times-check-20261102.txt` (the check's output; the recording itself,
    `docs/recordings/lap-times-353-20261102.jsonl`, is local and not committed).

## Session and weather check: laps, time and weather from iRacing (added 2026-11-03)

For race control's header and weather card. Runner:
console `session-record` (once a second).

- **Checked in the sim** (2026-11-03, iRacing 2026.10.01.01, the user's AI weekend at Autódromo Hermanos Rodríguez
  Grand Prix: a 600 s session iRacing calls "Warmup", then a 5-lap race; dynamic weather with rain): pass.
  - All 22 variables are there: `SessionLapsTotal`, `SessionLapsRemain`, `SessionLapsRemainEx`, `SessionTimeTotal`,
    `SessionTimeRemain`, `AirTemp`, `TrackTemp`, `TrackTempCrew`, `TrackWetness`, `Precipitation`,
    `WeatherDeclaredWet`, `Skies`, `WindVel`, `WindDir`, `RelativeHumidity`, `FogLevel`, `AirPressure`, `AirDensity`,
    `SolarAltitude` and the session ones.
  - **Laps:** unlimited is 32767 (the timed session). In the 5-lap race `SessionLapsRemainEx` went 5, 4, 3, 2, 1, 0
    as the leader completed laps: the leader's lap is total − remainEx + 1 (1 at the start, 5 on the last lap);
    `SessionLapsRemain` is one less.
  - **Time:** no single "unlimited": the lap race had `SessionTimeTotal` 86400 (remaining counting down from 86399),
    after the finish 604799 remaining. After the checkered flag the remaining time jumps (cool-down). The session info
    says `600.0000 sec` / `unlimited` laps for the timed session.
  - **Session states** in the race: 1 get in car, 2 warmup, 3 parade laps, 4 racing, 5 checkered, 6 cool down.
  - **Weather changed live:** `TrackWetness` 4 → 5 → 6 (lightly → moderately → very wet; 0 = unknown for a moment at
    the session change), `Precipitation` 0 → 0.82, `Skies` 2 → 3 (mostly cloudy → overcast), `TrackTempCrew` 19.4 to
    23.1 °C, `AirTemp` about 20 °C, `WindVel` 1.4 to 3.9 m/s, `RelativeHumidity` 0.86 to 0.95, `FogLevel` 0;
    `WeatherDeclaredWet` true throughout.
  - **Consequence:** laps sent only when limited (not 32767); time left shown only for a timed session (total below a
    day) while racing; wetness 0 left out.
  - **Recording:** `docs/recordings/session-20261103.jsonl` (local, not committed).

## Class names check: class names and colours from iRacing (added 2026-11-04)

For class names and colours from iRacing. Runner: console `classes-record`
(the session info's class fields whenever they change: per car `CarClassID`, `CarClassShortName`, `CarClassColor`,
`CarID`, `CarScreenName`, `CarScreenNameShort`; per session `EventType`, `Category`, `LeagueID`, `NumCarClasses`,
`NumCarTypes`; no names of people).

- **Questions:** is `CarClassShortName` filled in (hosted, league and AI sessions)? Is it short ("GT3") or long? Is it
  iRacing's class name, or the one the session admin gave the class? What does `CarClassColor` hold, and in which
  format? Do all cars of a class carry the same name and colour?
- **Checked in the sim, part 1** (2026-11-04, iRacing 2026.11.02.01, the user's AI race at Lime Rock Park Grand
  Prix, five classes of one car model each: Mazda MX-5 Cup, Ferrari 296 GT3, Cadillac V-Series.R, Porsche 911 Cup
  (992.2), Mercedes AMG GT4): partial.
  - **Name:** `CarClassShortName` is empty for every class in an AI session. `CarScreenName` (the car model, for
    example "Mercedes AMG GT4") is there.
  - **Colour:** `CarClassColor` is there for every class, as text `0xrrggbb` (for example `0xffda59`), the same on all
    cars of a class.
  - **Class ids:** the Mazda MX-5 Cup was class 74 here and in the results check of 2026-11-03 (another day, same
    track): the ids look like iRacing's own fixed class ids. The event: `EventType` Race, `Category` SportsCar,
    `LeagueID` 0, `NumCarClasses` 5.
  - **Recording:** `docs/recordings/classes-default.jsonl` (local, not committed).

- **Checked in the sim, part 2** (2026-11-09, iRacing 2026.11.02.01, a league's practice at Okayama International
  Circuit, the user as a driver without admin rights, three classes, 9 cars): pass.
  - **Name:** `CarClassShortName` is filled in: "M2", "GT4", "CUP". These look like the names the league gave its
    classes (all capitals, and a "GT4" class of two car models); not confirmed against the league's session setup.
  - **Class ids:** 89882, 89883, 89884, not iRacing's fixed ids (74 for the MX-5 Cup in the AI session): a class
    the league builds gets an id of its own.
  - **Colour:** as in part 1, text `0xrrggbb` (`0xff5888`, `0x33ceff`, `0xffda59`), the same on all cars of a class.
    (The recorder prints each value with its type, "0xff5888 (String)"; the type is not part of the value.) The pace
    car is class 11 with colour `0x000000` and no name.
  - **Same per class:** all cars of a class carried the same name and colour. `NumCarClasses` 4 counts the pace car's
    class.
  - **Consequence:** none for the code: the agent sends the name, the colour and the car models (0.12+), and race
    control shows the stewards' name first, then iRacing's.
  - **Recording:** `docs/recordings/classes-20261109-181721Z.jsonl` (local, not committed).

## Results check: iRacing's results through the finish (added 2026-11-03)

For race control's results and exports. Runner: console
`results-record` (every change of the session info's per-session values, `ResultsPositions` included, plus each car's
laps and live positions once a second; no names).

- **Checked in the sim** (2026-11-03, iRacing 2026.11.02.01, the user's 3-lap AI race at Lime Rock Park, 7 cars, one
  class): pass, with answers that change the plan's assumptions.
  - **When:** `ResultsPositions` change only when cars cross the line, about 2 s after each crossing (11 changes in the
    race), not in between. Before the green flag there are placeholder rows (everyone 0 laps, `Time` 0.001, 0.002, ...).
  - **`Time` is not the race time:** it is the gap to the leader in seconds (the leader 0), and `Lap` is the laps behind
    the leader (0 on the lead lap). Lapped cars carry a gap too (62.7 s and 72.3 s, one lap down).
  - **Positions:** `Position` counts from 1, `ClassPosition` from 0.
  - **Per car:** `LapsComplete`, `LapsLed`, `FastestLap`/`FastestTime` and `LastTime` (-1 = none) update with the
    crossings; `LapsDriven` and `Incidents` stay 0 until the results are official, then fill in (4 and 2 here).
  - **Official:** `ResultsOfficial` turned 1 in the cool-down, about 20 s after the leader's checkered flag (hosted AI
    session; an online session may take longer). The session also has `ResultsFastestLap` (car, lap, time),
    `ResultsLapsComplete`, `ResultsAverageLapTime`, cautions and lead changes.
  - **Out:** car #218 sat on pit road from lap 2 to the end; its `ReasonOutId` stayed 0 ("Running"). No retirement
    reason was seen.
  - **Recording:** `docs/recordings/results-20261103-151957Z.jsonl` (local, not committed).
- **Steps:** start a short AI race (3 to 5 laps) in iRacing, then
  `dotnet run --project src/PaceLaps.Agent.Console -- results-record` before the green flag. Drive or spectate; if you
  can, retire or get towed once. Let it run past the checkered flag and through the cool-down until the results are
  official, then type q. Afterwards export the race's results CSV from iRacing's results page (decision 4 of the
  plan) and keep both files.
