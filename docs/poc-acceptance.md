# Proof-of-concept acceptance run (backlog 1.3)

One run in a hosted AI session that exercises the proof-of-concept agent end to end: activation, self-test, ready and active, snapshots and events, and commands through the command pipeline with their status checklist. The "server" is an in-process stub; the real web service connection is roadmap step 3.

Values from spikes 4 to 14: chat line limit 250 (spike 7), pace laps 1 to 20 (spike 9), harmless command `!help` (spike 12), macro slots read from `app.ini` (spike 10), incidents from the driver list (spike 4).

## Setup

1. Hosted AI race like session A of the spikes: you are host and admin, borderless windowed, race running under green, from the garage or spectating. Neither iRacing nor the terminal elevated.
2. Optional, for the macro channel: set two free chat macro slots to `!pitclose$` and `!pitopen$` (for example slots 14 and 15), then **close and restart the sim**: it saves macros to `app.ini` only when it closes, and the self-test reads them from there.
3. In a terminal in the **`agent-poc`** folder (the proof-of-concept worktree):
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- run --macros "14=!pitclose;15=!pitopen" --league 1
   ```
   A hosted session has league id 0, so `--league 1` stands in for "the operator selected the league" (spike 11 fallback). Leave out `--macros` if you skip step 2.

The agent reads iRacing's chat box through the clipboard after every typed line (spike 15). It restores the clipboard's text afterwards, but not images or files on it, so keep nothing important there during the run. A line the sim left in the box gets one more Enter; anything else in the box is cleared and reported. `--no-chat-check` turns this off.

The console prints `[agent]` state changes and `[stub]` messages: hello, every 20th snapshot, events and every command status step. Everything is also written to `docs/poc-logs/poc-run-<utc>Z.jsonl`.

## Steps

Type each line at the `>` prompt. "Expect" is what should happen; note anything different.

| # | Type | Expect |
| --- | --- | --- |
| 1 | `status` | state Sleeping, channels all disabled. Nothing is sent while sleeping. |
| 2 | `activate` | Self-test steps 1 to 6. Answer the questions: the key-binding rule (yes, if no macro or control is on a plain key) and whether the `!help` reply appeared. Step 6 reads the macro slots from `app.ini`; it asks only if the file cannot be read. Then `[agent] SelfTest -> Ready`, `[stub] hello: Ready`, `[stub] joined`, `[agent] Ready -> Active`, `[stub] hello: Active`. |
| 3 | `status` | Active, executor True epoch 1, typed Ready, macro Ready (or Disabled), snapshots counting up. |
| 4 | `pit_close` | executing `channel macro slot 14` (or typed), result `sent ... not verifiable`. Pit lane closes in the sim. |
| 5 | `pit_open` | Same, pit lane opens. |
| 6 | `black_flag <car> drive_through` | result `verified ... black flag on #<car>`. |
| 7 | `clear <car>` | result `verified ... black flag cleared`. |
| 8 | `announce PoC test announcement` | result `sent`; the text shows in chat. |
| 9 | `announce !dq #12` | result `failed: rejected: text must not start with ! or /`. Nothing is typed. |
| 10 | `clear 12a` | result `failed: rejected: car must be one to three digits`. |
| 11 | `yellow` | result `verified ... caution flag`; `[stub] event ... FlagChange` with caution. |
| 12 | `yellow` | result `verified: caution already active, not thrown again`. Nothing is typed. |
| 13 | `pace_laps_set 3` then `pace_laps_add 1` | **After the pace car's first full caution lap** (spike 9: earlier it is refused). Both `sent ... not verifiable`; laps to green shows 3, then 4. |
| 14 | `restart_double` | Early in the caution. Result `verified` (pace mode) or `sent ... not seen within 10 s` (the sim updates it late; spike 3). |
| 15 | `wave_by <lapped car>` and `eol <car>` | each `verified` (waved around / end of line). |
| 16 | `epoch 0`, then `pit_open`, then `epoch` | `failed: stale epoch 0, current is 1`. Then commands use the agent's epoch again. |
| 17 | `dup` | The last command again with the same id: its earlier result is repeated with `duplicate command id, not executed again`; nothing is typed. |
| 18 | Watch the restart | `[stub] event ... FlagChange` to green. |
| 19 | `sleep`, wait 10 s, `status` | Sleeping; no `[stub]` lines while asleep. A command typed now is ignored. |
| 20 | `activate`, then `quit` | Self-test again and active again; then the run ends and prints the outbox path. |

Keep a note of anything that looked wrong in the sim (wrong car, a command not taking effect, focus not coming back, chat left open). Then tell the developer the run is done; the outbox file is all that is needed.

## Results

### Run 1, 2026-10-25 13:31 to 13:52 UTC (hosted AI race, 21 cars, agent 0c5e897)

Operator notes: `docs/poc-acceptance-results.txt`. Message log: `docs/poc-logs/poc-run-20261025-133101Z-trimmed.jsonl` (every message except snapshots and heartbeats, of which every 20th and every 10th are kept; the full 9.6 MB file stays local). An earlier attempt failed at self-test step 2 (hosted AI sessions have no session ids; fixed in 0c5e897): `docs/poc-logs/poc-run-20261025-132553Z.jsonl`.

- **Steps 1 to 14, 15a, 16 to 20: as expected.**
  - Self-test 6/6 twice, with the macro slots read from app.ini.
  - Macro channel for pit close and open.
  - Typed channel 270 to 300 ms with focus restored.
  - Verified: black flag, clear, caution, wave-around.
  - Sent: pit, pace laps, announce, restart (not verifiable).
  - Rejections: `!dq` announcement, `12a`, stale epoch, duplicate id.
  - Events: six AI incidents and the whole flag sequence. Sleep was silent; re-activation worked.
- **Step 15b: failed.** `!eol #7` stayed in the chat box twice: Windows accepted the keys and the Enter, focus came back, but iRacing did not send the line. The first attempt was correctly reported as "sent; end of line not seen within 5 s", but chat was left open with text. Followed up in spike 15.
- `wave_by 10` (not seen) was an operator slip (car #10 was not in the pace field; iRacing said so); the agent's result was right.
- **Fixed after the run:**
  - snapshots are no longer printed into the console (they broke up typing);
  - the diagnostic log path is printed at start (a process started from inside a packaged app (an MSIX app) writes to a redirected AppData folder);
  - a command whose effect was already showing (a second black flag on #12) is reported as sent, not verified.

### Run 2 (steps 4 to 15 again), 2026-10-25 14:55 to 15:09 UTC (agent 19226e0, chat box check on)

Message log: `docs/poc-logs/poc-run-20261025-145537Z-trimmed.jsonl`.

- **Every step as expected.** Self-test 6/6 twice. pit_close and pit_open went through the macros. Verified: the black flag and clear on #7, the caution, the wave-around of #5, and the end of line on #777. Pace laps and restart were sent. The rejections, the stale epoch and the duplicate behaved as in run 1. Sleep was silent and re-activation worked.
- **Chat box check:** after every typed line the box was empty. No line needed a second Enter, and no text was left in chat. The lost Enter of run 1 did not happen again, so the retry path is covered by unit tests only.
- **Cost:** a typed command now takes about 620 to 660 ms from executing to result, against about 300 ms before. The line itself still goes out at the same point; the rest is the check. The self-test's `!help` took 636 ms and 614 ms.
- **Flags:** at the green flag the sim briefly showed green together with one_lap_to_green and green_held (one frame, 0.1 s) before plain green.
