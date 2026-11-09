# App check

A manual check of the agent's app on a real PC, before a release. iRacing is needed only where a step says so; the
dev web service is needed for pairing (`docker compose up` in `../webservice`). The look of every screen can be
checked without clicking through the states: `pacelaps-agent.exe --render <folder>` (local builds, `docs/development.md`).

Start it with `dotnet run --project src/PaceLaps.Agent.App`, or run
`src\PaceLaps.Agent.App\bin\Debug\net10.0-windows\win-x64\pacelaps-agent.exe` after `dotnet build`. To keep your
own pairing, set `PACELAPS_AGENT_DATA` to another folder first (`docs/development.md`).

| # | Do | Expect |
| --- | --- | --- |
| 1 | Start it, not paired yet. | The window opens on the welcome: three steps, Pair, Start with Windows. The PaceLaps icon by the clock, grey badge, faded mark. |
| 2 | Start it a second time. | No second icon; the first one's window comes to the front. |
| 3 | Pair. (A local build asks for the address: `http://localhost:8081`, Get a code.) | The code in large characters, Copy the code, Open the pairing page (opens the browser), the address and the time left counting down. |
| 4 | Approve the code on the page (signed in as a steward). | The window turns to Status; a notification "Paired" at the bottom right. The rail's card shows the server and the agent id. |
| 5 | Close the window (X). Click the icon. | The window closes; the icon stays. A click opens it again on the status screen. |
| 6 | Right-click the icon. | The dark menu: the state on top (Sleeping), Open PaceLaps Agent, Activate, Settings, Open the log folder, Exit. |
| 7 | Settings: league id `abc`, Save. | Not saved; the error names `league`, in red above the buttons. Empty it, turn on the local failsafe, 45, Save: "Saved". Status shows the local failsafe tile "On". |
| 8 | Settings: Start with Windows on, Save; reopen Settings. | Still on (registry value "PaceLaps Agent" under HKCU\Software\Microsoft\Windows\CurrentVersion\Run). Turn it off and Save. |
| 9 | Settings: Use chat macros on. | The 15 slots and the app.ini field appear; Browse opens a file dialog. Turn it off again (Cancel). |
| 10 | Activate with iRacing not running. | The window switches to Self-test: step 1 fails in red, the line on top reads FAILED AT, Run the self-test is offered. The icon turns red; a red notification names the step. |
| 11 | With iRacing in a hosted session as admin: Activate. | Steps fill in one by one; the question "Did iRacing show the reply to !help…?" comes in its own window in front of iRacing, and the self-test screen shows it as a card. Yes: passed, the status screen turns amber (ready) or green (in a session). |
| 12 | In a session with race control: send a yellow from race control. | Last commands lists `!yellow …` with Verified and the time it took. A notification "You send the commands" came when this agent became the executor; it didn't take the focus from iRacing. |
| 13 | Exit from the menu while connected. | A question "The agent is connected to Race Control." with Sleep and exit; it sleeps, then the icon goes away. |
| 14 | Windows display scaling 150 %: start it again. | Sharp text and icons, nothing cut off, the tray icon's badge readable. |
| 15 | Update notice: `./scripts/publish.ps1 -Version 0.1.0 -Server https://racecontrol.pacelaps.com`, unzip, start that `pacelaps-agent.exe` (exit the other one first). | Within seconds a blue notification "Update available" and the menu line "Update available: <newest>", which opens pacelaps.com/download. |

Write down anything that looks wrong or reads badly, with the step number.
