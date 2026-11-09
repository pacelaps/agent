# Contracts

JSON schemas for the messages shared by the web service, the agent and the SimHub plugin. This folder is the source of truth for message shapes. The spec section "Interface contracts" explains them.

| Schema | Message |
| --- | --- |
| `common.schema.json` | Shared definitions (version, timestamp, car number, audience, condition) |
| `snapshot.schema.json` | Agent to server: snapshot, 1 to 2 Hz |
| `command.schema.json` | Server to agent: command (per-type parameter rules) |
| `message.schema.json` | Server to devices: message (audience, showAt) |
| `agent-messages.schema.json` | Agent to server: hello (with the session identity), session check, event (also the local failsafe's run), heartbeat (with the local failsafe it holds), command status, resync, self-test result |
| `agent-server.schema.json` | Server to agent: session check result, hello result and heartbeat result (with the macros policy, the local failsafe to hold, its rank and release), executor assignment, events ack, command list (long-poll), local failsafe document |
| `audit-export.schema.json` | Audit log export of one hash chain (verifiable by anyone on pacelaps.com/verify; reference checker `verify_audit_export.py`) |
| `agent-pairing.schema.json` | Agent and server: device-code pairing (request, code, poll, status, token) |
| `device-messages.schema.json` | Devices and server: clock ping/pong, state snapshot, shown report (provisional), race state (the race by car number, for the driver page; spec-feedback 45) |
| `procedure.schema.json` | Procedure document (editor, engine, agent's offline copy) |
| `penalty-catalog.schema.json` | A league's penalty catalog, exported and imported on the Penalty catalog page |

## Rules

- Every message carries a version `v`. Receivers ignore unknown fields (the message schemas allow additional properties). The procedure schema is strict: unknown fields are rejected.
- iRacing-derived field names are provisional until the Phase 0 spikes confirm them.
- Event data shapes are fixed for all three event types: incident, flag_change (`flags`, `previous`, `sessionNum`, `sessionTimeSec`) and session_change (the session identity), 2026-10-25.
- The agent messages are final as of roadmap step 3 (2026-10-26): `agent-messages.schema.json` (agent to server) and `agent-server.schema.json` (server to agent). With sub-session id 0 (hosted AI sessions), the server binds an agent to the league's open session; the operator names the league (spec-feedback item 13). Step 5 (2026-10-27) adds, all optional so the previous agent still validates: the macros policy, the local failsafe the agent should hold (`localFailsafe`, fetched from `GET /api/v1/agent/local-failsafe`) with its rank and release, the agent's own local failsafe state in the heartbeat, the `self_test_result` message (`POST /api/v1/agent/self-test`) and the `local_failsafe` event.
- Session flag names are a fixed vocabulary (`common.schema.json` `flagName`), normalised by the agent; `green` is derived, not iRacing's green bit (spike 3, 2026-10-25). Pace laps are 1 to 20 (spike 9); a chat line is cut at about 254 characters, so `announce.text` is at most 250 and the optional `message` at most 230 (spike 7).
- Audiences (step 2 review, 2026-10-25): scope `all`, `class` (with `classes`), `car` (with `cars`) or `stewards`; the list that does not belong to the scope is not allowed. Car numbers are one to three digits, because they appear in channel names. Messages carry a `cursor` (resync position) and may list the ids they `supersedes` (ended states, revoked penalties); banner text is at most 120 characters. The state snapshot has a `banners` list. Shown reports carry `receivedAt`, `offsetMs` and `rttMs` so the spread can be judged against the offset's accuracy.
- Clock pongs carry `serverReceivedAt` as well as `serverTime` (2026-10-26, optional): devices use the NTP formula, so the server's processing time does not skew the offset.
- Audit exports (2026-10-26): one export per hash chain (a session's, a league's entries without a session, or the global chain). Each entry's hash is the SHA-256 of its canonical JSON without the hash (keys sorted, no whitespace, UTF-8 unescaped, nulls included), and prevHash links it to the entry before (64 zeros for the first). `python verify_audit_export.py EXPORT.json` checks an export (numbers are hashed as written: `1.0e-7` stays `1.0e-7`); `validate.py` runs the same check on the examples, so a schema-valid but altered export is rejected. People check exports on pacelaps.com/verify (the website's `public/verify.js`, in their browser), with the same answers on the same examples (website `tests/fixtures/audit`, copies of these). **A new `algorithm` needs its check on the website first** (`ALGORITHMS` in `public/verify.js`, never removing an old one): the website's test fails while an example here uses a format it doesn't know. A `resync` can carry the agent's own view of an outage (`outage {from, to}`, optional). Devices read the open session's chain as this export document at `GET /api/v1/device/audit` while the league's audit log is public (every entry, nothing hidden; spec-feedback item 19), and the session's `event.{id}.audit` channel rings with `audit.written` (no data) when entries are added.
- Penalties (2026-10-26, step 4): a message may carry an optional `detail` (at most 300 characters) shown under the banner, for a penalty the catalog's explanation. Snapshot cars may carry optional per-car `flags` (black, disqualify, waved_around, end_of_line); the agent sends them from step 5, and the server then marks black-flag penalties served on its own.
- Procedure banners (2026-10-27, spec-feedback 24): a procedure run's sticky banners belong to the run. A sticky `show_message` replaces the run's earlier sticky banners of the same message type; `clears: true` withdraws all of them; when the run ends with banners still up, the server withdraws them with a short one-off message. The templates clear their banners at the green flag.
- Track (2026-11-01, race control's track map card, spec-feedback item 38): the snapshot's `session` may carry `track` (`id` = WeekendInfo.TrackID, one per layout; `name`, `config`, optional `lengthM`), sent by agent 0.8 and newer. Optional: without it race control shows the circle map instead of the track map.
- Lap times (2026-11-02, race control's lap times card, spec-feedback item 39): the snapshot's `session.track` may carry `sectorStarts` (SplitTimeInfo, lap distance of each sector start, the first 0), and each car `lastLapTime` and `bestLapTime` (iRacing's times in seconds; absent while iRacing has none). A new event `sector_crossings` bundles, about once a second, when cars crossed a sector start (`car`, `sector` index, `lap` = laps completed after it, `sessionTimeSec` interpolated, optional `pit`, `off`, `jump` since the car's previous crossing). All optional: without them the card shows nothing for that agent. Sent by agent 0.9 and newer.
- Results (2026-11-03, results and exports, spec-feedback item 42): a new event `session_results` carries a session's results as iRacing's session info has them, whenever they change (about 2 s after line crossings, and when they turn official in the cool-down): `sessionNum`, `official`, the leader's `lapsComplete`, the session's `fastestLap`, and per car `position`, `classPosition` (from 1), `laps`, `lapsBehind`, `gap` (seconds behind the leader; iRacing gives no race time), `lastLap`, `bestLap`, `bestLapNum`, `lapsLed`, `incidents` (meaningful once official) and `out` (null while running). Optional: an older agent sends none.
- Session and weather (2026-11-03, race control's header and weather card, spec-feedback 40): the snapshot's `session` may carry `laps` (`total`, `remaining`: a lap-limited session; iRacing's unlimited 32767 left out), `time` (`totalSec` below a day, `remainingSec`: a timed session) and `weather` (`airTempC`, `trackTempC`, optional `wetness` and `skies` as names, `precipitationPct`, `declaredWet`, `windSpeedMs`, `windDirDeg`, `humidityPct`, `fogPct`). All optional; agent 0.10 and newer.
- Replay for incident review (2026-11-03, spec-feedback 41): `replay_jump` may carry `car` (the camera follows it); new `replay_live` (no parameters) returns to live. Both are view commands: sent to one agent a steward chose, executor or not, run without the epoch check. Per-car telemetry stays live during a replay.
- Penalty catalog (2026-11-05): the Penalty catalog page exports the catalog as `penalty-catalog.schema.json` and imports it (or a bare list of entries). Entries match by code: new codes are added, existing ones updated, others left alone. Strict, like the procedure schema. Only the web service reads it.
- Changing a contract: update the schema, update or add examples (valid and under `examples/invalid/`), run `python validate.py`, update the spec's contract section and tell the user, because the agent and the plugin must follow.

## Semantic procedure rules (not expressible in JSON Schema)

`validate.py` implements these; the web service must implement them in PHP and test against the same example documents:

- Phase ids are unique.
- Every `goto` target, `branch` target and `countdown.onCancel` (other than `end`) names an existing phase.
- Every phase is reachable: it is the first phase, it has triggers, or a `goto`, `branch` or `countdown.onCancel` leads to it.
- A phase has an optional list of `triggers`; any one starts it. The triggers of the first phase start the procedure; triggers of a later phase are watched only while the procedure runs. A `timer` trigger counts seconds since the procedure started, or for the first phase since the session started.
- `iracing_command` blocks only use commands the league has enabled (league setting, not checked by the script), and their `args` are valid for that command (`validate.py` checks the args against `command.schema.json`).
- A procedure designated as a local failsafe uses only: announce, iracing_command, pits, wait, wait_until, branch, goto, end, and only conditions the agent can evaluate offline (flag_is, pace_car_on_track, combined with all, any, not).
- A published version is immutable. Editing creates a new version.

`example-schemas.json` says which schema each example file is checked against (by file name prefix). `validate.py` and the web service's contract test both read it; add a prefix there for a new message type.

In `validate.py`, files whose name contains `local-failsafe` are checked against the local failsafe rules.

## Examples

Valid documents are in `examples/`. Documents that must be rejected are in `examples/invalid/`. The agent and the plugin should test their serialization and parsing against these files.

The local failsafe procedure has no triggers: the delay without a server connection is an agent setting, and the agent starts the first phase when it has passed.
