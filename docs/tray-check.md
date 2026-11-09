# Tray app check (step 5.4)

A manual check of the tray app's screens. iRacing is not needed; the dev web service is (`docker compose up` in `../webservice`). With the sim, the tray app is part of the step 5.6 test script.

Start it with:

```
dotnet run --project src/PaceLaps.Agent.Tray
```

or run `src\PaceLaps.Agent.Tray\bin\x64\Debug\net10.0-windows\win-x64\pacelaps-agent.exe` after `dotnet build`. Its icon appears in the notification area (it may be under the ^ arrow; drag it out to keep it visible).

| # | Do | Expect |
| --- | --- | --- |
| 1 | Start it. Hover over the icon. | Grey icon. Tooltip: "PaceLaps Race Control: Sleeping, ..." (not paired yet, or Activate to run the self-test). |
| 2 | Start it a second time. | No second icon; the status window of the first one opens. |
| 3 | Left-click the icon. | Status window: State Sleeping, iRacing not running, server and pairing, local failsafe; "Last self-test: None yet"; recent log lines. Close hides it. |
| 4 | Right-click, Pair with a server. Enter `http://localhost:8081`, Get a code. | A code in large type and a link. The link opens the pairing page in the browser. |
| 5 | Approve the code on the page (signed in as a steward). | The window says "Paired as agt_... with http://localhost:8081/." The status window shows "paired as agt_...". |
| 6 | Right-click, Settings. Change the league id to `abc`, Save. | Not saved; the error names `league`. Set it back (or empty), tick the local failsafe, set 45 s, Save. The status window shows "Local failsafe: on (after 45 s)". |
| 7 | Settings: tick Start with Windows, Save. Open it again. | Still ticked. (Registry value "PaceLaps Agent" under HKCU\Software\Microsoft\Windows\CurrentVersion\Run.) Untick it and Save. |
| 7b | Settings: "Send typed commands without bringing iRacing to the front" is ticked (the default). Untick it, Save, open again. | Unticked; `config.json` has `"typedWithoutFocus": false`. Tick it again and Save. |
| 7c | Settings: "Look for a new version once a day" is ticked (the default). | `config.json` has `"checkForUpdates": true` after a Save. A local build never checks; step 11 uses a release build. |
| 8 | Right-click, Activate (iRacing not running). | Blue while the self-test runs, then a notification "Self-test failed: Step 1 ..." and a red icon. The status window shows the failed step; the state is Sleeping again. |
| 9 | Right-click, Open the log folder. | Explorer opens `%LOCALAPPDATA%\PaceLaps\Agent\logs`. |
| 10 | Right-click, Exit. | The icon goes away. |
| 11 | Update notice: `./scripts/publish.ps1 -Version 0.1.0 -Server https://racecontrol.pacelaps.com`, unzip it, start that `pacelaps-agent.exe` (exit the other one first). | Within seconds a notification "Update available: PaceLaps Agent <newest> is available." and the menu's top line "Update available: <newest>...". It opens a window with what's new; Open the download page opens pacelaps.com/download. The Sleep item reads "Sleep (disconnect from Race Control)". The log has "Update check: <newest> is available". |

Write down anything that looks wrong or reads badly, with the step number.
