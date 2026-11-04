# Integration test results (roadmap step 3)

## Run 1: 2026-10-26, 14:52 to 15:07 UTC

- **Result: pass.** Every step of `docs/integration-test.md` behaved as expected (operator's report). The records confirm it, and show four findings for the degraded case (realtime server down) plus one race at activation; none changed an outcome in this run.
- Setup: hosted AI race at Lime Rock Park, iRacing build 2026.10.01.01, agent `0.1.0+a6d1fca` (`agt_pbc7ejf9hztp`) with `--league 15457` and macros 14 and 15, web service on the `integration` branch (development stack, `http://localhost:8081`, vendor in a volume: about 0.3 s per request), race control on this PC and on a second device.
- Records: run log `docs/poc-logs/integration-run-20261026-145211Z.jsonl`; server: session 3 (`command_events`, `agent_events`, audit log).

### What happened

| Step | Result |
| --- | --- |
| Activate (self-test) | Passed; step 2: league 15457 selected by the operator, no iRacing session ids. The hello result bound the agent to the open manual session "Lime Rock Park" (spec-feedback item 13) 0.2 s after the self-test. |
| Executor | Chosen on the page, epoch 1; unchanged for the rest of the run (no handover). |
| `!yellow` "Debris turn 3" | Verified (caution flag). Button to the agent's receipt 165 ms, to the result 855 ms (typed channel). |
| Announce | Sent, typed, 844 ms. |
| `!pitclose` / `!pitopen` | Sent through macro slots 14 and 15: button to result 160 to 265 ms (the agent itself takes 1 ms). |
| Flag and incident events | 11 events, each stored once, about 85 ms after the agent saw them (caution, one to green, green held, green; an incident of car 6). |
| `offline 40` | Lost after 3.6 s (audit `agent.lost`, last heartbeat 15:01:39.4). `!pitclose` during the outage failed at once: "The executor is not connected (no heartbeat)". |
| Event during the outage | The operator's own `!yellow` at 15:01:49.5 was buffered and resent with the resync at 15:02:20.7 (`lastAckedSeq` was the incident before it); stored with its original event time. `agent.reconnected`: lost for 37.7 s. |
| After the outage | `!pitopen` sent normally (same executor, same epoch). |
| Reverb stopped (optional step) | The websocket got 502s; four commands reached the agent by long-poll and all ended `sent`. Reverb back: the websocket subscribed again by itself. See findings 1 to 3. |
| `sleep` | The agent went silent; the server marked it lost 3.9 s later. |

Totals: 14 commands issued, 14 results (13 sent or verified, 1 failed as intended during the outage); every command's six checklist steps are recorded. Commands came from two steward pseudonyms (this PC and the second device). The roster of league 15457 got 21 session entries: the AI drivers.

### Findings

1. **A down realtime server slows every request that broadcasts by about 2 s** (web service). With the Reverb container stopped, its name no longer resolves, and every broadcast waits for cURL's 2 s timeout (`Resolving timed out`). Command status reports, snapshots that change the flags and the steward's actions all broadcast, so each took over 2 s. The agent sends its status reports one after another, so the third report of a command was recorded up to 10 s after the agent sent it (pit_open at 15:06:11: received by the executor at 13.4 s, recorded at 19.6 s). With a few more commands the 10 s acknowledgement timeout would have failed a command that had been executed. Proposal: a circuit breaker in `ResilientReverbBroadcaster` (after a failure, skip broadcasts for a few seconds and log it), so requests do not wait while Reverb is down.
2. **Long-poll commands arrived about 2 s late** (web service). The dispatcher marks a command `sent_to_executor` only after the broadcast returns, and the long-poll hands out only `sent_to_executor` commands. Proposal: record the step and status before broadcasting (the agent may answer before the broadcast returns anyway).
3. **The agent's long-poll looped at about 13 requests a second for 15 s** (agent). While a status report was waiting (finding 1) the server kept returning a command the agent already had; the agent ignores repeats but polled again at once. Proposal: wait a moment before polling again when an answer brought nothing new.
4. **A false loss of 0.55 s** (agent and web service). At 15:04:50, right after Reverb stopped, heartbeats queued behind snapshots that were slowed by finding 1 and the server counted the agent lost. Proposal: heartbeats get their own lane in `HttpServerLink`; finding 1's fix removes the cause.
5. **`agent.activated` was written twice, 4 ms apart** (web service). The hello and the first heartbeat bound the agent at the same moment. Harmless here; proposal: bind under a row lock on the agent and check again inside the transaction.

### Fixes (2026-10-26, after the run)

All five findings are fixed with tests: web service `20134a0` and `f948009` (circuit breaker, opened only when Reverb cannot be reached, not when it refuses one broadcast), `503cc94` (sent_to_executor before the broadcast), `cfc3871` (binding under a row lock); agent `b065f25` and the follow-up commit (heartbeat lane; the long-poll pauses 1 s when an answer comes back at once with nothing new). Not yet re-run in the sim.
