# Developing PaceLaps Agent

How to build, run, test and package the agent. The overview is in the `README.md`.

## Build and run

Needs the .NET 10 SDK (see `global.json`). Windows x64 only.

```
dotnet build
dotnet test
dotnet run --project src/PaceLaps.Agent.Console -- help
```

The app (manual check before a release: `docs/app-check.md`):

```
dotnet run --project src/PaceLaps.Agent.App
```

To check the screens without clicking through the states, a local build draws them in example states to PNG files
at 100 % and 150 % (also the tray menu, the notifications and the self-test question): `pacelaps-agent.exe --render <folder>`
(from `src/PaceLaps.Agent.App/bin/...`; release builds refuse it). The app's look is WPF resource dictionaries in
`src/PaceLaps.Agent.App/Themes` (colours = Race Control's dark tokens, checked by `ColoursTests`; Inter, Barlow Condensed and
JetBrains Mono built in from `Assets/Fonts`).

It starts Sleeping: no connection to the server until Activate, which reads the settings and the credentials, connects and runs the self-test; Sleep or a failed self-test closes the connection. The window (status, self-test, settings, about; first start and pairing until paired) exists only while open; closing it leaves the agent running by the clock. The icon's menu has Open, Activate or Sleep, the update, Settings, the log folder and Exit. One instance per Windows session; a second start brings the first one's window up. The console's `run --server` runs on the same code (`Hosting/AgentRuntime`).

With a Race Control server from the console (steps: `docs/integration-test.md`), pair once, then run:

```
dotnet run --project src/PaceLaps.Agent.Console -- pair --server http://localhost:8081
dotnet run --project src/PaceLaps.Agent.Console -- run --server http://localhost:8081 --league 15457
```

The settings live in `%LOCALAPPDATA%\PaceLaps\Agent\config.json` (server, league for hosted sessions, macro channel and slots, local failsafe and its delay, app.ini path); `run` reads it and its options override it. `config` shows it, `config --init` writes one with every setting:

```
dotnet run --project src/PaceLaps.Agent.Console -- config --init
```

Local failsafe (opt-in, `localFailsafe.enabled` in `config.json`): the agent keeps a copy of the procedure the league designates (`local-failsafe.json` next to the settings, so it survives a restart) and, when the server has not answered for the delay (default 30 s) plus 15 s per rank, runs it once per outage if a race (or practice or qualifying, where the league arms them) is under way and no caution is out. It never ends itself: a steward releases it from the race control page. `status` in the console and the tray app's status window show whether it is on, which version it holds and whether it is running; `offline SECONDS` simulates the outage.

The console program is `pacelaps-agent-cli.exe`. `run` without a server (none in the settings file, or `--stub`) is the proof-of-concept agent with an in-process stub server (steps: `docs/poc-acceptance.md`):

```
dotnet run --project src/PaceLaps.Agent.Console -- run --macros "14=!pitclose;15=!pitopen" --league 1
```

The Phase 0 spike runners:

```
dotnet run --project src/PaceLaps.Agent.Console -- probe
dotnet run --project src/PaceLaps.Agent.Console -- spike1 --slot 7
dotnet run --project src/PaceLaps.Agent.Console -- spike2 --mode borderless
dotnet run --project src/PaceLaps.Agent.Console -- spike3 --car 12 --car2 33
dotnet run --project src/PaceLaps.Agent.Console -- spike9          # and spike4 to spike14, see help
```

Spikes need Windows with iRacing in a hosted AI session; the manual steps are in `docs/spike-procedures.md`. Spike logs go to `docs/spike-logs/` (JSON Lines). A rolling diagnostic log goes to `%LOCALAPPDATA%\PaceLaps\Agent\logs`. Everything else (typed channel sequence, mapping, spike evaluation) is unit-tested without iRacing.

CI: `.github/workflows/ci.yml` builds and tests on a Windows runner, then builds the package and checks it (the zip is kept as a workflow artifact for 7 days).

## Package and release

The operator's package is one self-contained exe (no .NET needed on the PC; not trimmed, WPF does not allow it) in a zip with `docs/operator-guide.md` as `README.txt`, plus a SHA-256 checksum file. Build it locally:

```
./scripts/publish.ps1                  # artifacts/pacelaps-agent-<version>-win-x64.zip (development version)
./scripts/publish.ps1 -Version 0.5.0 -Server https://app.pacelaps.com   # a release: pairs only with that server
```

The script runs the packaged exe with `--version` (it prints the version and exits without showing anything) and fails if it does not start. A release: push a tag `vX.Y.Z` on main; `.github/workflows/release.yml` tests, builds the package with that version and the production server built in (its pairing screen and settings show that server; local builds take any) and attaches the zip and checksum to a GitHub release (a prerelease below 1.0 or with a suffix). The exe is not signed yet: SmartScreen asks once, as the operator guide says. Updates are manual (exit, replace the exe); settings and pairing stay in `%LOCALAPPDATA%\PaceLaps\Agent`.

## Layout

```
src/PaceLaps.Agent.Core/       no iRacing dependency: contracts, states, self-test, command renderer and executor,
                                      snapshots, events, clock offset, typed chat sender, agent host
src/PaceLaps.Agent.IRacing/    IRSDKSharper adapter (SimConnection, SimDataAdapter, macro and replay channels), Win32 focus, scancode keyboard
src/PaceLaps.Agent.Hosting/    shared by the console and the tray app: settings, credentials, logging, AgentRuntime,
                                      AgentStatus, the app's views as plain data (StatusView, SelfTestView; unit-tested, no WPF)
src/PaceLaps.Agent.App/        the app (WPF): window, tray icon and menu, notifications; pacelaps-agent.exe
src/PaceLaps.Agent.Console/    console (run, pair, config; stub server) and spike runners, pacelaps-agent-cli.exe
tests/PaceLaps.Agent.Tests/    xunit v3 on Microsoft.Testing.Platform, fakes and a fake clock
docs/spike-results.md                 results per spike
docs/spike-procedures.md              what the operator does in iRacing
docs/poc-acceptance.md                the proof-of-concept acceptance run
docs/app-check.md                     manual check of the app
docs/operator-guide.md                the operator's guide (README.txt in the package)
scripts/publish.ps1                   builds the package (artifacts/, ignored by git)
contracts/                            copy of webservice/contracts (scripts/sync-contracts.ps1), used by the tests
Directory.Packages.props              pinned package versions (IRSDKSharper 1.3.0)
```
