# Spike procedures (manual, in iRacing)

Step-by-step instructions for the spikes that need the sim. The runners log to `docs/spike-logs/` and the results go into `docs/spike-results.md`. Every runner can simply be run again; each run writes a new log file.

## Before any spike

1. **Build** in a normal (not administrator) terminal in the `agent` folder:
   ```
   dotnet build
   ```
   The runner is then `src\PaceLaps.Agent.Console\bin\x64\Debug\net10.0-windows\win-x64\pacelaps-agent-cli.exe`. The examples below use `dotnet run --project src/PaceLaps.Agent.Console -- <command>`, which does the same.
2. **iRacing must run at the same privilege as the runner.** Normally neither runs as administrator. If iRacing runs elevated, Windows silently drops the runner's keystrokes. `probe` reports both.
3. **Telemetry:** `irsdkEnableMem=1` in iRacing's `app.ini` (the default).
4. **Session for all three spikes:** host one session with AI drivers, using the league's AI-enabled cars.
   - Short track with short laps (about a minute), around 15 AI cars, one class.
   - Practice short; race long enough for the whole spike 3 sequence (30 laps or more).
   - Full-course cautions **on**.
   - You are the host and admin. **Spectate, do not drive**, if the session allows it. If you have to join as a driver, stay in the garage and note this.
5. **Window mode:** in the sim's graphics options, choose **borderless windowed** (spike 2 also needs one run in fullscreen).
6. **Check the setup:**
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- probe
   ```
   It shows the build, session, `DriverIsAdmin`, the car numbers, the window mode hint, elevation and keyboard layout. Paste the output to the developer if anything looks wrong.

## Spike 1: macros without focus (about 3 minutes)

1. In the sim's options, set a **free chat macro slot** (note its number, 1 to 15) to exactly:
   `/rc spike1 macro test$`
   The trailing `$` makes the macro send immediately instead of leaving the text in the chat box.
2. Arrange the windows so iRacing's chat area stays visible while another window has focus. Use a second monitor, or a small, non-maximised browser window that does not cover the chat.
3. Run (replace 7 with your slot):
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike1 --slot 7
   ```
4. Answer **y** to the control trigger. iRacing comes to the front, the macro fires once, and the console comes back. Answer whether the message appeared. If it did not, the macro text or slot is wrong: fix it and start again.
5. During the 10 second countdown, **click into the browser** and leave keyboard and mouse alone. The runner fires the macro 20 times, every 3 seconds.
6. Count the `spike1 macro test` lines in iRacing chat. Then switch to the console and answer the questions: how many appeared, and whether the chat box was ever left open.

If iRacing rejects `/rc` (an error line in chat instead of the message), use plain chat instead: in an AI session nobody else reads it. Set the macro to `spike1 macro test$` and add `--macro-text "spike1 macro test$"`.

## Spike 2: typed chat reliability (about 5 minutes per window mode)

**Borderless windowed (this run decides pass or fail):**

1. iRacing in borderless windowed, in the session from above.
2. Arrange the windows as in spike 1 so the chat stays visible.
3. Run:
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike2 --mode borderless
   ```
4. During the countdown, **click into the browser**. Do not touch keyboard or mouse for about 2 minutes. 50 messages like `/rc s2 bw 07/50 a3f9` are sent, one every 2 seconds. Each time, iRacing comes to the front briefly and focus goes back to the browser.
5. Watch the chat and note any **numbers that are missing** or **garbled** (wrong or missing characters). Answer the questions in the console when it beeps.

**Fullscreen (recorded, not a pass criterion):**

1. Switch iRacing to fullscreen in its graphics options.
2. Run with a longer focus timeout, because a fullscreen mode switch can take seconds:
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike2 --mode fullscreen --focus-timeout 5000
   ```
3. Same as above. You will probably not see chat while the browser has focus. Afterwards, check the chat history in iRacing if you can, and say so in the notes if you could not check.

If `/rc` is rejected, add `--prefix none` (plain chat). If characters arrive wrong, run again with `--key-delay 10` and tell the developer.

## Spike 3: admin commands in the AI session (30 to 45 minutes)

1. Back to borderless windowed. The race session must be running under green.
2. Choose a target car from the `probe` car list: a car in the middle of the field, not the leader. Optionally choose a second car that is (or will be) a lap down, for the wave-by.
3. Run (replace the numbers):
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike3 --car 12 --car2 33
   ```
4. For each step the console shows the command, **when** to send it, and **what to watch**. Wait until the "when" condition holds, then press Enter in the console to send.
   - iRacing stays in front after the command so you can watch. Live telemetry changes are printed in the console.
   - The watch ends on its own (beep) or when you switch to the console and press Enter.
   - Then answer **d** (as documented), **n** (no effect) or **o** (something different) and write a short note on what happened.
5. If a command's syntax is wrong (chat shows an error, or `!help` lists a different form), choose **e** at the step and type the corrected command. Both texts are logged.
6. `s` skips a step and `q` ends the run. The log keeps everything up to that point. If the session ends early, start a new race and run again with only the missing steps: edit a copy of the steps file and pass it with `--steps <file>`.
7. The order follows one race:
   - Green flag: pit close and open, black flag drive-through (let it be served), black flag stop-and-go, then clear it.
   - **!yellow**: wait until the pace car has picked up the leader.
   - During that caution: pace laps absolute (4) and relative (+2), wave-by of the second car, end of line of the target car, restart double, then restart single.
   - Send both restart commands **early**, well before "one lap to green". In run 1, a restart command sent at one to go was confirmed in chat but did not apply. The runner now warns about this.
   - Watch the restart to green and note how many laps the caution lasted.

### Spike 3 re-check: restart single (one caution, about 10 minutes)

Run 1 did not show `!restart single` taking effect. In a running race under green:

```
dotnet run --project src/PaceLaps.Agent.Console -- spike3 --car <num> --steps src/PaceLaps.Agent.Console/bin/x64/Debug/net10.0-windows/win-x64/Spikes/spike3-restart-recheck.json
```

The steps are: `!yellow`, then `!pacelaps 5` (time for the next two), then `!restart double` right away, then `!restart single` at least two laps before green. Let the last watch run through the restart. Answer whether the field restarted single file.

### Before typed-channel tests: key bindings

iRacing acts on a bound key even while the chat box is open. In spike 2 run 1, a macro bound to the plain key `2` fired on every message. On the PC that runs the agent, do not bind chat macros or sim controls to plain character keys (letters, digits, `#`, `+`, `!`, space). Use modifier combinations or wheel buttons instead.

## Spikes 4 to 14

All runners are started the same way: `dotnet run --project src/PaceLaps.Agent.Console -- <command>`. The short form `spikeN` is used below. Each run writes its own log; a failed or interrupted run can simply be repeated. Steps files are found by name (for example `--steps spike9-steps.json`).

The spikes are grouped by the sim session they need. Within session A the order matters only where noted.

### Session A: hosted AI race (about 90 minutes)

Same setup as spikes 1 to 3: hosted session with AI, you as host and admin, borderless windowed, race of 40 laps or more, full-course cautions on, Crew Chief installed. Spikes 12, 7, 5, 9 and 6 can be run from the garage; spikes 14 and 4 need you driving.

**Spike 12: harmless command (3 minutes).** Under green, run `spike12`. Three commands go out: `!help`, `!help yellow` and an `/rc` message. Note exactly what appears for each (text, colour, channel such as `@ADMIN` or `@PRIVATE`). No second person is available, so who else sees it stays open.

**Spike 7: maximum chat length (10 minutes).** Run `spike7`. It sends ruler texts like `/rc ....10........20...` of 60 to 400 characters, first with `/rc`, then as plain chat. After each, type the **last number you can read** in chat (0 if the message did not appear), and whether it was on one line, wrapped, or split. Plain chat goes to everyone in the session; with only AI in it nobody else reads it.

**Spike 5: per-car fields (6 minutes).** Run `spike5`. It records every car for 5 minutes and writes a recording to `docs/recordings/`. Twice or three times during the run, press Enter: it prints its standings (position, class position, lap, pit road). Compare with the sim's standings or relative and answer. Spectating or sitting in the garage is fine.

**Spike 9: caution extension (25 minutes, two cautions).** Run `spike9`. The steps:
1. `!yellow`; end the watch once the pace car leads the field.
2. `!pacelaps 2` to shorten the caution.
3. Three steps that are **armed**: press Enter, and the runner sends the command itself the moment "one lap to green" shows. In order: `!pacelaps +2`, `!yellow`, `!pacelaps 3`. For each, note whether the caution extended.
4. Watch the restart (ends by itself at the green flag).
5. Second caution: `!yellow`, then `!pacelaps 99`, `!pacelaps 20` and `!pacelaps 1`. Note what the chat replies and what laps to green shows. Watch the restart.

**Spike 6: replay jump while live (5 minutes).** Re-run after 2026-10-25: the runner now also sums laps completed over all cars, which shows whether the per-car data follows the replay. Use a race at least four minutes old, so a jump of 180 s back stays in the session. **Do not drive**: be in the garage or spectating. Run `spike6`. Five times it jumps your local replay back (in the current session, to the previous session, and three quick jumps) and returns to live. After each jump answer whether the screen jumped and whether it came back to live.

**Spike 14: Crew Chief (15 minutes, you drive).** Start Crew Chief. Get in your car and drive under green. Run, with your own car number:
```
dotnet run --project src/PaceLaps.Agent.Console -- spike14 --car <your car> --auto
```
With `--auto` the steps run by themselves. A beep comes 3 seconds before each command:
- `!yellow`, then `!pacelaps 2`, then the restart;
- `!black` drive-through on your car, `!clear`;
- `!black` stop and go on your car, `!clear`.

Keep a note of what Crew Chief (and iRacing's own spotter, if you hear it) says and when. At the end, switch to the console and give the verdicts. If you would rather stop between steps, leave out `--auto` and use Enter in the console during the caution.

**Spike 4: incident latency (10 minutes, you drive).** Run `spike4` while in your car, then drive. Cause one off-track (1x), and later a contact with an AI car (usually 4x). The console shows each count change as the telemetry and as the session info (what others see) report it. Press Enter in the console when done, and describe what you did.

### Session B: AI session without the pace car (15 minutes)

**Spike 13.** When hosting, look in the session options for a setting that disables the AI pace car. If there is none, that is already the result; note it and skip the rest. Otherwise run `spike13` under green:
- `!yellow`: watch what the field does without a pace car;
- `!pacelaps 2`;
- the restart.

### Session C: an official testing session (5 minutes)

**Spike 8: name and admin detection without admin rights.** Join a testing session (you are not admin there) and run `spike8`. Answer whether you are admin (no) and whether your name shows. Spikes 1 to 3 already showed `DriverIsAdmin` = 1 as host.

**Spike 11: session identity.** In the same session run `spike11` and answer `o` (official/test). It records the ids. A league session is needed for a pass, so the spike stays open until one is available.

### Offline: macro configuration (10 minutes, two sim starts)

**Spike 10.**
1. Start the sim. In the chat macro settings, set a free slot (note the number) to `spike10markerA$`. Close the sim completely.
2. Run `spike10 --marker spike10markerA`. It searches `Documents\iRacing` read-only and shows where the marker is.
3. Start the sim, change the same slot to `spike10markerB$`, close the sim.
4. Run `spike10 --marker spike10markerB`, then again `spike10 --marker spike10markerA`, which should no longer be found.
5. Optional, for the key-binding check: bind that macro slot to a key combination you will recognise, close the sim, and run step 4 again. The runner lists files that look like key-binding files.

Afterwards, set the macro slot back to what you use for racing.

## Spike 15: chat Enter and chat readback (added after the acceptance run, 15 minutes)

In the acceptance run, `!eol #7` stayed in the chat box twice: iRacing got the text but ignored the Enter. Car #7 was the only target whose number is the start of other cars' numbers (#77, #777); no suggestion list appears, so that is a guess. This spike measures it. It also tests whether the agent can read the chat box through the clipboard, so it never leaves text there unnoticed.

1. A hosted AI race under green, like session A. You are the host. Ideally there are cars like #7 and #77. Spectate or sit in the garage, and keep iRacing's chat visible.
2. Copy anything important off the clipboard first: the spike uses it. Text on it is restored; images and files are not.
3. Run:
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike15
   ```
4. Pick the **suspect car**: one whose number is the start of another car's number (for example #7 when #77 exists). Then pick a **control car**: one whose number is not the start of any other. The runner lists the cars and warns if a choice does not fit.
5. **Phase A, about 1 minute:** the runner types `spike15 readback test` into chat without pressing Enter, then tries to read it back and close the chat box. Answer whether the chat box ended up closed and empty.
6. **Phase B, about 5 minutes:** under green, do not touch keyboard or mouse. The runner sends `!black #<car> D` and `!clear #<car>` for both cars in four forms: plain, 100 ms pause before Enter, 300 ms pause, trailing space. Each form goes out twice. After each send it checks telemetry and reads the chat box. A line printed as `LEFT: '...'` was still in the chat box, and the runner cleared it.
7. **Phase C, optional, about 5 minutes:** answer `y`, and the runner throws `!yellow`. Press Enter once the pace car leads the field. Then it sends `!eol` to both cars.
8. At the end, note what you saw in chat: left-over lines, and iRacing's replies.

## Spike 16: chat without bringing iRacing to the front (added 2026-10-28, about 20 minutes)

the other tool sends commands without iRacing coming to the front; the agent's typed channel brings it forward for every line. Part 1 watches what the other tool does, passively. Part 2 tries ways that need no focus switch. Nothing attaches to iRacing's process (it has anti-cheat): the monitor uses system-wide hooks in its own process.

**Setup:**
1. A hosted AI session (practice is enough). You are the host and admin. Do not drive: sit in the garage or spectate. Keep iRacing's chat visible.
2. Run iRacing and this program both normally, not as administrator.
3. Copy anything important off the clipboard first: part 2 uses it. Text on it is restored; images and files are not.
4. Have the other tool ready, connected to the session.

**Part 1, monitor (about 5 minutes):**
1. Run:
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part monitor
   ```
2. When it says it is watching, use the other tool only (do not type anywhere else). Send, with a few seconds between them:
   - a race control message (public), for example `spike16 rc test`;
   - a private message to one car;
   - one harmless command if the other tool has one (for example pit lane open while it is already open).
3. Each time, note whether the message appeared in chat. The monitor's window prints every event as it happens; you do not need to read it.
4. Back in the monitor's window: press Enter to add a note (for example "sent the public message at about 18:05"), then Q to end. Answer the questions at the end.

**Part 2, send (about 5 minutes):**
1. Run:
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part send
   ```
2. When the countdown starts, click into another window (your browser) and do not touch keyboard or mouse. Keep iRacing's chat visible.
3. It sends 12 numbered admin-only lines, `/rc s16 <variant> <number>`, three per variant:
   - **A** and **B**: typed into iRacing by window messages, Enter as a key (A) or as a character (B). No focus switch.
   - **C**: pasted by a window message. No focus switch.
   - **D**: iRacing to the front, paste, Enter, back (the way the Stream Deck plugin iRaceDeck does it), for comparison.
4. After each line, it briefly brings iRacing to the front to read the chat box back (as in spike 15). So expect iRacing to flash forward once per line; that is the check, not the variant.
5. At the end, answer which numbers appeared in chat (for example `1-3,10-12`), whether the chat box was ever left open, and anything else that happened in the sim (a camera change, a menu, a key binding firing).

### Spike 16, part soak: the window-message way at length (about 25 minutes)

Part send showed that variant A sends a line without the focus switch. Before it replaces the typed channel's focus switch, this tests it at length: 50 lines, real admin commands, special characters, and while you drive.

**Setup:**
1. A hosted AI **race** (the caution and the black flag need one) under green. You are the host and admin. Sit in the garage or spectate for phases 1 and 2; phase 3 has you drive. Keep iRacing's chat visible.
2. Run iRacing and this program normally, not as administrator. Close the other tool, so that only this program sends.
3. **Optional, the binding check:** in iRacing's options, set a chat macro slot to `/rc BINDING FIRED$`, and bind that macro to the plain key **5** (no modifier). The lines contain 5s (05, 15, 25, ...). If the binding ever fires, that line appears in chat. Remove the binding after the test.

**Run:**
```
dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part soak
```

1. **Phase 1, about 2 minutes:** when the countdown starts, click into your browser and do not touch keyboard or mouse. It sends 50 numbered admin-only lines, one every 2 s:
   - `Q 01` to `Q 25`: no waits;
   - `W 26` to `W 50`: with short waits.

   At the end, enter the numbers that did **not** appear (blank if all did), how often `BINDING FIRED` appeared, and whether the chat box was ever left open.
2. **Phase 2, about 2 minutes:** pick an AI car. After the countdown (click into your browser again), it sends `!black #<car> D` and `!clear #<car>` three times each and checks each in telemetry. Then it sends a line with special characters:

   `Zeichen: äöüß é # ! ? , . : ; ( ) - + = @ 60`

   Answer whether it appeared exactly. Then, if you agree, it throws `!yellow Spike 16 test caution, please ignore`. Answer whether iRacing showed the message. You can let the caution run out afterwards.
3. **Phase 3, about 1 minute (optional):** answer yes, click into iRacing within 15 s and drive. It sends 10 more lines (`R 51` to `R 60`), one every 3 s, while you drive. After the two beeps, stop safely and answer which numbers did not appear, and whether anything disturbed driving.

### Spike 16, part driving: in the car (about 10 minutes)

In the soak, posted lines arrived while you were in the garage but not while you were in the car. The other tool's messages do arrive while you drive, and its chat box seemed to show for a few frames. This part times the other tool in the car, then tries waits of different lengths.

**Setup:**
- A hosted AI race (practice works too). You are the host and admin.
- iRacing and this program run normally, not as administrator.
- The other tool runs, but send from it **only in phase A**.

**Run:**
```
dotnet run --project src/PaceLaps.Agent.Console -- spike16 --part driving
```

1. **Phase A, about 2 minutes:**
   - Get in the car (on track, or parked in your pit stall).
   - From the other tool, send an `/rc` message and a public message, a few seconds apart.
   - Back in this window, press Enter.
   - Answer which messages appeared, and whether the chat box was visible for a moment.
   - The runner prints how long the other tool kept the chat open.
2. **Phase B, about 1 minute:**
   - Within 15 s, drive into your pit stall and stop there, staying in the car.
   - Then click into your browser and do not touch anything.
   - It sends 15 numbered admin-only lines, `/rc s16 d<form> NN`, in five forms:
     - `d0`: no waits;
     - `d1`: 50 and 100 ms;
     - `d2`: 150 and 300 ms;
     - `d3`: 300 and 500 ms;
     - `dS`: 150 and 300 ms, delivered as sent messages instead of posted ones.
   - Then enter the numbers that did **not** appear.
3. **Phase C, about 1 minute:**
   - Within 15 s, click into iRacing and drive out of the pits.
   - It sends 10 more lines (the five forms twice) while you drive.
   - After the two beeps, stop in a safe place. Answer which numbers did not appear, and whether anything disturbed driving.

## After a run

Tell the developer which spikes you ran. The logs are in `docs/spike-logs/`; you do not need to send anything else. A rolling diagnostic log is also written to `%LOCALAPPDATA%\PaceLaps\Agent\logs`.
