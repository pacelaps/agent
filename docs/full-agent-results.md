# Full agent test in the sim: results (step 5.6)

Run on 2026-10-28 by the user, alone, in an AI hosted race at Lime Rock Park, with the production service
(sha-a5e5d0b), the released agent v0.5.0 (`0.5.0+22c0718`) and the race control page on a second device. Script:
`docs/full-agent-test.md`. Sources: the agent's log, the block log of the outage proxy, and the session's audit
export (`audit-15457-s2.json`, league 15457, session 2). Times are UTC.

**Result: passed.** Resync, handover there and back, and the local failsafe all worked by blocking the agent's
network, as the step 5 exit criteria ask. Six findings, listed below; none blocked the test.

## What happened

| Part | What | Evidence |
| --- | --- | --- |
| A | Installed from the release zip, paired with production as `agt_y8nqfhpnod6f`, league 15457 and the local failsafe (30 s) set. | Agent log 07:52 to 08:01. |
| B | Self-test passed (typed channel ready, `!help` in 694 ms), Ready, then Active once a session was open. The steward chose the executor (epoch 1, 08:05:49). `!yellow` with a message verified in telemetry 1.5 s after it was issued; `!pitclose`, `!pitopen` and an announcement typed in 650 to 690 ms each, ending at "sent" (telemetry cannot show them). | Audit seq 3 to 15. |
| C | Block 08:30:42 to 08:31:22 (40 s). The server saw the agent lost after 3 s (last heartbeat 08:30:41.3, lost 08:30:45.6) and left the session without executor (epoch 4). The agent noticed after 5 s. The caution typed by hand during the block was buffered; at the 30 s mark the local failsafe saw the caution and did not act. After the block: resync with 1 event and the outage window 08:30:46.7 to 08:31:22.1 (`agent.outage_reported`), executor again at epoch 5 within 1 s. | Audit seq 21 to 25; agent log 08:30:47 to 08:31:24. |
| D | The fake agent `agt_3n0lkbvjwpvf` joined as second agent and held the local failsafe copy (v1, 4 phases). Block 08:40:16 to 08:40:56: the server handed the executor over to the fake agent 2.6 s after the block began (epoch 6), which executed the steward's Pit close. The real agent stayed observer after the block (hold-down). When the fake agent was stopped, the role came back (08:42:37, epoch 7). | Audit seq 26 to 31, 37, 38. |
| D/E | During the block in D the real agent, cut off for 30 s, started the local failsafe (finding 4): `!restart single` (08:40:45), `!yellow` with the message (08:40:56, verified). Used for part E instead of a new block: `!pacelaps +1` at each one lap to green, 08:44:54, 08:46:40 and 08:48:25; each time the caution went on for another lap. A steward released it at 08:48:33; the agent stopped at once and typed nothing more; the caution ended at its next one lap to green. | Audit seq 32 to 36, 39 to 43; agent log. |
| F | Sleep, Exit. | Audit seq 44, 45. |

**Open question answered:** iRacing honours repeated `!pacelaps +1` at one lap to green within one caution, at least
three in a row (no cap seen). Recorded in `docs/spike-results.md` (spike 9).

## Findings

1. **Release builds should always pair with the production server** (the user). The pairing screen offers a free
   address. Proposal: the release build carries the production address; its pairing screen and settings show it fixed.
   Local builds keep the free field.
2. **No executor at first activate** (the user). The first agent of a fresh session stayed without the executor role
   until a steward chose it 4 minutes later; after a loss, the server promotes an agent at once ("an agent became
   available"). This follows the spec ("Stewards pick the executor in the panel"), but is inconsistent.
   spec feedback 29.
3. **Pit open and close have no effect during a caution** (the user; iRacing behaviour). The commands were typed and
   reported "sent". Step 9 of the test is corrected to run them under green. spec feedback 33.
4. **The local failsafe also starts when only the agent lost its network** while the server and another agent kept
   running the race (part D): a full course yellow race control did not ask for. The agent cannot tell its own loss
   from the server's. spec feedback 30.
5. **The agent applied an older epoch after a newer one** (agent bug): on joining at 08:29:55 the hello answer
   (epoch 2, observer) and the executor assignment (epoch 3) crossed, and the log shows 2, 3, 2, 3 within 0.5 s. It
   ended right, but in the other order the agent would have rejected the executor's commands as stale until the next
   heartbeat answer. Fix: within one session, ignore an epoch lower than the current one.
6. **`!restart single` cannot be verified under green**, so the failsafe waited the full 10 s for a pace mode that only
   shows in a caution before it typed `!yellow` (08:40:45 to 08:40:56). Fix: under green, report restart commands as
   "sent" at once.

Minor, for the record:

- The agent stamps event and command status times with the PC's clock (here 0.6 to 0.7 s ahead of the server), but
  converts the outage window to the server's clock; some audit entries show a result "recorded" before it happened.
  spec feedback 31.
- Sleep looks like a loss in the audit (`agent.lost`, "lost its connection"), because a sleeping agent sends nothing.
  spec feedback 32.
- While the fake agent was executor, the audit's session time came from its invented telemetry (191 s instead of
  about 2300 s). A test artefact of the fake agent, not of the service.
- At 08:02:28 the server released the agent: a steward opened a new session (session 2) while it was in session 1; it
  joined session 2 seven seconds later. One snapshot in between was refused (409). Expected behaviour.
- The Windows Firewall on the test PC refused program rules (error 0x2), so the blocks went through a local proxy
  (`scripts/outage-proxy.mjs`) instead; verified with the agent's HTTP and websocket clients.
- Reading the agent's log from an MSIX app: it sees a redirected copy of `%LOCALAPPDATA%`; the real file was
  copied from the user's own shell.

## Re-check with v0.5.1 (2026-10-28, after the fixes)

Same setup: production `sha-8fa469a`, agent v0.5.1 (`0.5.1+b153e9a`) from the release, session 3 ("Lime Rock Park 2"),
audit export `audit-15457-s3.json`. All nine steps as expected; every finding fixed:

| Finding | Seen |
| --- | --- |
| 1. Release pinned to production | Pairing and settings show `https://racecontrol.pacelaps.com/`, not editable. |
| 2. First executor (spec feedback 29) | The agent became executor 0.4 s after it joined the fresh session: `executor.handover`, reason "first agent of the session" (seq 3). No steward choice. |
| 3. Pit commands in a caution (33) | Pit close and open under green, then the yellow; the panel showed the hint while the caution was out. |
| 5. Older epochs | The agent's log shows epochs 1, 2, 3 in order, no flip. |
| 6. Restart under green | Local failsafe: `!restart single` reported "sent" at once, `!yellow` verified 0.8 s later (before: 10 s). |
| 31. Times on the server's clock | Command results are now stamped 0.07 to 0.08 s before the server recorded them (before: up to 0.6 s after), and the local failsafe's events fit the server's timeline. |
| 32. Sleep | `executor.handover` reason "sleeping" and `agent.released` reason "sleeping" (seq 22, 23); no `agent.lost`. |

Handover again 0.03 s after the server saw the agent lost (block of 40 s), and back when the fake agent stopped.
