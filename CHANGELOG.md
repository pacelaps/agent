# Changelog

What changed in each release of the PaceLaps agent, for the people who run it. The newest comes first. Each release's
heading is `## <version> – <yyyy-mm-dd>`; `scripts/upload-downloads.ps1` publishes this file as
`agent/changelog.json` for the website's download page, so keep the format.

## Unreleased

- Less load on the server: while another agent runs race control, this one sends the sim's state every 2 seconds instead of twice a second. It switches back at once if it takes over.

## 0.13.0 – 2026-11-10

- A new app window in the PaceLaps look: the status at a glance (iRacing, Race Control, typing, the local failsafe), the last commands with their result, the self-test step by step, and the settings.
- A welcome on the first start, and pairing with the code in large characters, a copy button and the time left.
- Notifications in the PaceLaps look at the bottom right of iRacing's screen. They never take the focus from the sim.
- The PaceLaps icon by the clock, with a badge in the state's colour.
- Tells you when a new version is out: a notification and "Update available" in the menu, which opens the download page. Turn it off in the settings.
- Team races: a driver swap no longer shows up as an incident.
- The agent is now free software under the GNU GPL version 3; the source is on GitHub.

## 0.12.0 – 2026-11-04

- Sends your session's car classes with iRacing's names and colours, so race control and the driver page show them as in the sim.
- Sends iRacing's results during the race and once they are official, for the results in Race Control.
- Incident replays now play on any active agent, also on one without session admin rights (view only).
- Sends the session's length (laps or time left) and the weather, for race control's header and weather card.
- Times every car's sectors, for race control's lap times card.
- Sends the track layout, for race control's track map.
- Shorter, clearer texts in the tray app, the self-test and the guide.

## 0.7.0 – 2026-10-31

- Types commands without bringing iRacing to the front, so you can keep driving. If a command doesn't show up in the sim, the agent types it once more the old way.

## 0.6.0 – 2026-10-28

- New name: PaceLaps Race Control.

## 0.5.1 – 2026-10-28

- After a handover between agents, a late answer no longer makes an agent act as the one sending commands.
- Restarts under green are confirmed at once, so the local failsafe's caution no longer waits 10 seconds.
- Sleep tells Race Control right away, so another agent can take over at once.
- Times in reports use Race Control's clock.

## 0.5.0 – 2026-10-28

- First release: the tray app to pair, activate, sleep and run the self-test; race data to Race Control; commands from race control typed into the iRacing chat; and the optional local failsafe.
