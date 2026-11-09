# PaceLaps Agent

The PaceLaps Agent is the Windows program that connects an iRacing session to
[PaceLaps Race Control](https://pacelaps.com/race-control), a race control service for iRacing leagues. It runs on a
session admin's PC: it reads the session through iRacing's SDK and types race control's commands into the iRacing
chat, as an admin would.

- **Download and guide:** [pacelaps.com/download](https://pacelaps.com/download) and
  [the agent guide](https://pacelaps.com/docs/race-control/agent).
- **No race logic here.** Race Control decides; the agent executes structured commands from a fixed list (black
  flags, cautions, pit lane, pace laps and so on), checks every parameter, and reports what happened.
- **Asleep until you activate it.** While it sleeps it doesn't talk to Race Control at all. Activating runs a
  self-test in your session first.
- **Doesn't modify iRacing.** It uses iRacing's SDK (telemetry, session info, chat and replay messages) and the admin
  commands iRacing gives session admins. PaceLaps is not affiliated with iRacing.

## Build and test

Needs Windows x64 and the .NET 10 SDK (`global.json`).

```
dotnet build
dotnet test
dotnet run --project src/PaceLaps.Agent.Tray
```

More (the console, packaging, releases, the layout of the code): [docs/development.md](docs/development.md).
The results of the checks in iRacing that the design rests on: [docs/spike-results.md](docs/spike-results.md).

## Licence

PaceLaps Agent is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
License version 3 ([LICENSE](LICENSE)), the licence of [IRSDKSharper](https://github.com/mherbold/IRSDKSharper), which
it uses to read iRacing. The libraries it contains and their licences are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) (the .NET runtime's own notices:
`licenses/dotnet-runtime-notices.txt`). The release zip carries all three. Race Control, the service the agent talks
to, is a separate program and not covered by this licence.

## Contributing and security

Bug reports and ideas are welcome at support@pacelaps.com; see [CONTRIBUTING.md](CONTRIBUTING.md). Please report
security problems privately: [SECURITY.md](SECURITY.md).
