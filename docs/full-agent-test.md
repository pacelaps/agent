# Full agent test in the sim (step 5.6)

Step 5's exit criteria: handover, resync and the local failsafe verified by blocking the network. One run, alone, in
an AI hosted **race** with the production service (https://racecontrol.pacelaps.com), the released agent v0.5.0 and the
race control page on a second device (phone or tablet). It also answers an open question: whether repeated
`!pacelaps +1` at one lap to green keep extending one caution, or whether iRacing caps them.

Afterwards the developer reads the agent's log, `block-agent.log` and the server's records (audit log, agent events, command
results; read-only) and writes the result to `docs/full-agent-results.md`.

Times: about 60 to 75 minutes. Write down anything that differs from "Expect", with the step number.

## Before

- Production runs the web service's current main (sha-a5e5d0b or newer): the footer or `deploy-pacelaps.log` on
  the host shows it.
- On the race control site, signed in as a steward of your league:
  - **Procedures:** copy the template "Local failsafe (default template)" and publish it. If the league has an older
    copy, copy the template again: the new one adds a pace lap at one lap to green while the caution is out.
  - **Settings:** Local failsafe = the copy you just published. Save.
  - **League page:** Open session (any name, for example "Full agent test"). A hosted AI session has no ids of its
    own, so the agent joins the league's open session; without one it waits ("no session is open in league ...").
- Download `pacelaps-agent-0.5.0-win-x64.zip` and its `.sha256` from the agent repository's release v0.5.0.
- Exit any agent that is running (tray icon, Exit).
- The outage proxy cuts only the agent off from the network (Node.js, already on this PC). Open a PowerShell window,
  start it and leave it open for the whole test; the blocks are typed there:
  ```
  node scripts\outage-proxy.mjs        # in the agent folder
  ```
  The agent is started through it (step 2), so iRacing, the browser and the fake agent keep their network.

## A. Install and pair

| # | Do | Expect |
| --- | --- | --- |
| 1 | In PowerShell: `Get-FileHash <zip> -Algorithm SHA256`. Compare with the `.sha256` file. | The same hash. |
| 2 | Unzip into a new folder (for example `Documents\PaceLaps`). In a second PowerShell window start it through the proxy: `$env:HTTPS_PROXY = 'http://127.0.0.1:8899'; & '<folder>\pacelaps-agent.exe'` | SmartScreen may ask: More info, Run anyway. A grey icon in the notification area. The proxy window shows tunnels once the agent talks to the server. (No .NET needed: say whether this PC has .NET installed, if you know.) |
| 3 | Right-click the icon, Pair with a server: `https://racecontrol.pacelaps.com`, Get a code. Approve the code on the site's Agents page. | "Paired as agt_...". Note the agent id. |
| 4 | Settings: League id = your league's iRacing league id (a hosted session has none). Tick the local failsafe, 30 s. Save. | Saved. The status window shows "Local failsafe: on (after 30 s)". |

## B. Activate and run commands

| # | Do | Expect |
| --- | --- | --- |
| 5 | In iRacing: host an AI **race** session, join it as admin, borderless or windowed, sit in the car on the grid or in the pits. | |
| 6 | Tray: Activate. Answer the self-test's question about the !help reply. | Blue, then a "Self-test passed" notification, then green. |
| 7 | Race control page on the second device: open the session. If no agent is executor yet, choose yours in the Executor list (a fresh session waits for a steward: spec feedback 29). | Your agent: connected, executor, typed channel ready, "Local failsafe: on, holds v..." (the copy you published). Notification "Executor". |
| 8 | Start the race. Let it go green. Page: Pit close, then Pit open, then an announcement. | Each in the chat, each checklist complete (pit commands end at "sent"; iRacing ignores them during a caution, so they come first). |
| 9 | Page: Yellow with a message. | `!yellow` with the message in the chat; the caution comes; the action's checklist ends verified. Let the caution end and the race go green again. |

## C. Resync: the agent loses its network for 40 s

The blocks are typed in the proxy window. A block works like a dead network: the agent's connections stop carrying
data and new ones get no answer.

| # | Do | Expect |
| --- | --- | --- |
| 10 | Green flag. Proxy window: `block 40`. | Within about 5 s the page shows the agent **lost** and the red notice that the executor is not connected. Within about 15 s the tray icon turns red ("server unreachable"). iRacing keeps running. |
| 11 | During those 40 s: in iRacing, type `!yellow` in the chat yourself. | The caution comes (iRacing does it; race control cannot reach the agent). |
| 12 | Wait until the proxy says unblocked. | Within about 10 s: agent connected again, the notice gone, **Recent agent events** shows the caution with its time **during** the block. Notification "Server reachable again". |
| 13 | Let the caution end and the race go green again. | |

The local failsafe does not start here: a caution is out after step 11, and it never acts during one. If it started
before you typed `!yellow` (more than 30 s into the block), note the time and go on.

## D. Handover to a second agent and back

The developer starts a second agent on this PC (the web service's fake agent: it joins your league's hosted session and types
nothing, it only reports). Tell the developer when you are at this point; it runs:

```
docker compose exec app node tools/fake-agent.mjs --url=https://racecontrol.pacelaps.com --league=<iRacing league id> --sub-session=0 --store=storage/app/fake-agent-prod.json
```

| # | Do | Expect |
| --- | --- | --- |
| 14 | Approve the fake agent's pairing code on the Agents page (the developer tells you the code). | Page: a second agent in the session, connected, **not** executor. |
| 15 | Green flag. Proxy window: `block 40`. | Within about 5 s: your agent lost, and the notice "executor handed over from agt_(yours) to agt_(fake) (...)". Your tray app shows no longer executor once it is back. |
| 16 | While blocked: page, Pit close. | The fake agent executes it (it types nothing, so the pits stay open in iRacing; the checklist shows the fake agent's result). |
| 17 | Wait for Unblocked. | Your agent connected again, **not** executor (no switching back on its own). If the block lasted longer than the local failsafe's delay, your agent has started it meanwhile (it cannot tell its own network loss from a server outage: spec feedback 30); then go on with part E from step 21. |
| 18 | Tell the developer to stop the fake agent. | Within about 5 s: "executor handed over ... to agt_(yours)". Notification "Executor". Page: Pit open, so iRacing matches again. |

## E. Local failsafe: the agent loses its network during green

| # | Do | Expect |
| --- | --- | --- |
| 19 | Green flag, the race running. Proxy window: `block` (until you type `unblock`). Note the time. | Page: agent lost. |
| 20 | Wait. | About 30 s after the block: the agent types `!restart single` and `!yellow Full course yellow, single file behind pace car, race control lost connection`. The caution comes. Notification "Local failsafe started", red icon. |
| 21 | Watch the caution laps. At **one lap to green** each time: | The agent types `!pacelaps +1` and the caution gets one more lap. Count the extensions. Let it extend **at least three times** (more if you can). If an extension does not take, note which one and what iRacing said in the chat. |
| 22 | Proxy window: `unblock`. | Page: agent connected again, and the red line "agt_... runs the local failsafe since ... UTC: it keeps acting in iRacing until it is released", with Release. The agent keeps extending meanwhile. |
| 23 | Page: Release, confirm. | "Released, waiting for the agent to stop", then the line goes. The agent types nothing more; the caution ends at its next one lap to green and the race goes green. |

## F. End

| # | Do | Expect |
| --- | --- | --- |
| 24 | Tray: Sleep. | Grey icon. Page: the agent leaves the session (or shows sleeping). |
| 25 | Tray: Exit. Proxy window: `quit`. Leave iRacing. | |

## Afterwards

Tell the developer you are done, with the numbers of anything that differed and the extension count from step 21. The developer
reads:

- the agent's log: `%LOCALAPPDATA%\PaceLaps\Agent\logs\agent-<date>.log`
- the blocks: `%LOCALAPPDATA%\PaceLaps\Agent\logs\block-agent.log`
- on the server (read-only): the session's audit log (`agent.lost`, `agent.reconnected`, `executor.handover`,
  `agent.outage_reported`, `failsafe.local_*`, `command.result`), agent events and command results

and writes `docs/full-agent-results.md`, the extension answer into `docs/spike-results.md` (and the specification
if iRacing caps them), and the backlog.

If the proxy window was closed, the agent has no network: start the proxy again (blocks end with it), or exit the
agent and start it normally.
