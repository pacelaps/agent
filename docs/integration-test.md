# Integration test run (roadmap step 3)

One run with the real agent, the web service and iRacing together: pairing, activation in a hosted AI session, the executor choice, `!yellow` with a message, an announcement, `!pitclose`, a forced disconnect with an event during the outage, and the resync. Afterwards the developer reads the run log and the server's records (status checklist, audit log, events) and writes up the result.

What is new compared with the proof of concept (`docs/poc-acceptance.md`): the commands come from the race control page, not from the console, and the server decides whether the agent is active (spec-feedback item 13: in a hosted session the agent joins the league session a steward opened).

## Setup

1. **Web service.** In a terminal in `webservice/`:
   ```
   docker compose up -d
   ```
   Then check that it answers quickly (PowerShell; `curl.exe`, because plain `curl` is PowerShell's `Invoke-WebRequest`):
   ```
   curl.exe -s -o NUL -w "%{time_total}s\n" http://localhost:8081/up
   ```
   It should print well under 1 second. **If it takes several seconds, restart Docker Desktop** and check again: on 2026-10-26 the Windows file sharing slowed every request to about 5 s, and with that the server would count the agent as lost (it expects a heartbeat every second; three missed = lost).
2. **Browser.** Open `http://localhost:8081`, sign in, and open your league (SCT, iRacing league 15457). Open a session on the league page, for example "Integration test" (this closes the previous one). Then open **Race control** for the league and keep it visible.
   The roadmap's exit criterion wants the panel **on a second device** (laptop, tablet or phone). For that the development server must be reachable on the local network, as for the phone test in step 2.4: in `webservice/.env` set `DEV_HTTP_BIND=0.0.0.0` and `SESSION_SECURE_COOKIE=false`, run `docker compose up -d`, and open `http://<this PC's address>:8081` on the other device. Set both back afterwards (`127.0.0.1`, `true`); the development sign-in must not stay open on the network.
3. **iRacing.** A hosted AI session as for the proof of concept: you are host and admin, borderless windowed, race running under green, you spectate. Neither iRacing nor the terminal elevated. A hosted session has no league id, so the agent is told the league with `--league 15457`.
4. **Pair the agent** (once per PC). In a terminal in `agent/` (branch `integration`):
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- pair --server http://localhost:8081
   ```
   Open the address it prints, enter the code, approve. It answers `Paired as agt_...`. The token is stored encrypted for your Windows user (`%LOCALAPPDATA%\PaceLaps\Agent\credentials.bin`).
5. **Start the agent:**
   ```
   dotnet run --project src/PaceLaps.Agent.Console -- run --server http://localhost:8081 --league 15457
   ```
   Add `--macros "14=!pitclose;15=!pitopen"` if those macro slots are still set from the proof of concept. The console prints `[agent]` (state, events, status steps) and `[server]` lines; everything also goes to `docs/poc-logs/integration-run-<utc>Z.jsonl`.

Note: the agent puts the cars it sees into the league roster (as session entries). In an AI session these are the AI drivers. You can delete them on the roster page afterwards.

## Steps

Type the console lines at the `>` prompt; click the buttons on the race control page. "Expect" is what should happen; note anything different, with the time.

| # | Do | Expect |
| --- | --- | --- |
| 1 | Console: `status` | Sleeping. The race control page lists no agent. |
| 2 | Console: `activate` | Self-test steps 1 to 6. Step 2: `league 15457 (selected by the operator), no iRacing session ids`. Step 3: clock pings to the server, offset near 0 ms (same PC). Then `[server] active in Integration test, epoch N`. |
| 3 | Race control page | Without reloading: under **Agents** your agent with your iRacing name, `connected`, channels (typed: ready ...). The session line shows `Race · Racing · <n> cars · flags: green` (or what the sim shows). |
| 4 | Page: choose your agent in **Executor** | The epoch goes up by one. Console `status`: executor True with that epoch. |
| 5 | Page: type `Debris turn 3` into the yellow message field, click **!yellow** | The action shows all steps: sent to server, received by server, sent to executor, received by executor, executing, result **verified**. In the sim: full course caution with the message. Console: the status steps and an event `FlagChange` with caution. Page: **Recent agent events** lists the flag change. |
| 6 | Page: type `Integration test announcement`, click **Announce** | Result **sent**; the text appears in the iRacing chat. |
| 7 | Page: **!pitclose**, then **!pitopen** | Each ends at **sent (not verifiable)**; the pit lane closes and opens in the sim. |
| 8 | Wait until the race is **green** again | Page: flags green; a new flag change event. |
| 9 | Console: `offline 40` | Within about 4 s the page shows the agent as **lost** and a red notice that the executor is not connected. The console says requests fail. |
| 10 | During those 40 s: page **!pitclose** | Fails at once: `The executor is not connected (no heartbeat)`. Nothing is typed. |
| 11 | During those 40 s: type `!yellow` **yourself** in the iRacing chat | Caution in the sim. The console prints the `FlagChange` event; it waits in the buffer (the server is "unreachable"). |
| 12 | Wait for the 40 s to end | Console: `[server] reachable again: resync` and `[agent] resync: 1 events after ...`. Page: agent **connected** again, the notice is gone, **Recent agent events** shows the caution with its time **during** the outage. |
| 13 | Page: **!pitopen** | Works again (same executor, same epoch): sent; pit lane opens. |
| 14 | Optional, the long-poll fallback: in `webservice/`, `docker compose stop reverb` | The agent's websocket drops (diagnostic log). The race control page stops updating live: reload it by hand for this step. |
| 15 | Optional: page **!pitclose** (reload the page to see the result) | Reaches the agent anyway, by long-poll: result sent, pit lane closes. Then `docker compose start reverb` and reload the page. |
| 16 | Console: `sleep` | Sleeping: the agent sends nothing more. After about 4 s the page shows it as lost. |
| 17 | Console: `quit` | Prints the run log path. |

## Afterwards

Tell the developer the run log path (or just that you are done; the newest `integration-run-*.jsonl` is used) and anything that differed from "Expect", with the step number. The developer reads the run log, the server's status checklist and events (`command_events`, `agent_events`) and the audit log (`agent.activated`, `executor.chosen`, `agent.lost`, `agent.reconnected`, `command.result`), and records the result in `docs/integration-results.md`.
