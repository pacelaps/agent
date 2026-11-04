PaceLaps Race Control: operator guide
==================================

The agent runs on an iRacing session admin's PC and types race control's commands into the iRacing chat. While it
sleeps, it sends and receives nothing.

You need Windows 10 or 11 (64-bit), iRacing, admin rights in the session and a steward account for the league.


Install
-------

1. Unzip the folder anywhere, for example to Documents\PaceLaps.
2. Start pacelaps-agent.exe. If Windows says "Windows protected your PC", click "More info", then "Run anyway"
   (the program isn't signed yet). To check the zip: Get-FileHash <zip> -Algorithm SHA256 and compare with the
   release's .sha256 file.
3. An icon appears next to the clock (maybe under the ^ arrow). Its colour shows the state:
     grey   sleeping
     blue   self-test running
     amber  ready, no session yet
     green  in a session
     red    a problem: hover over the icon

Run iRacing and the agent the same way: both normally, or both as administrator.


Pair (once per PC)
------------------

1. Right-click the icon, "Pair with a server...".
2. Click "Get a code".
3. Open the link, sign in as a steward and enter the code.


Settings
--------

Right-click the icon, "Settings...". Changes apply at the next Activate.

- League id: only for hosted sessions.
- Macros: optional. Enter the slot texts exactly as in iRacing.
- Local failsafe: off by default. Turn it on only if your league asks. It also starts when only your own connection
  is down. A steward ends it in race control.
- "Type without bringing iRacing to the front": on by default. Turn it off only if commands stop arriving.
- Start with Windows: off by default. It starts sleeping.


In a session
------------

1. Join the session in iRacing as an admin.
2. Right-click the icon, "Activate". The self-test types !help and asks whether the reply appeared.
3. Green icon: you're in. If this agent is the executor, a notification says so. You can drive; iRacing only comes
   to the front for the self-test and the rare retry. Use borderless windowed mode.
   No chat macro or control on a plain key (letter, digit, #, + or space).
4. After the session: right-click, "Sleep".

If the self-test fails, left-click the icon to see why.

Without admin rights (as a spectator), the agent is "view only": it never sends commands, but stewards can play
incident replays on it.


When something is wrong
-----------------------

- Hover over the icon to see the problem.
- Left-click it for the status window.
- "Open the log folder" in the menu: send the newest agent-*.log if asked. Logs contain no passwords or tokens.
- Server unreachable: nothing to do. Events are sent once it's back.


Update
------

Exit the agent, replace pacelaps-agent.exe, start it again. Pairing and settings stay.


Remove
------

1. Untick "Start with Windows" in the settings.
2. Exit the agent and delete its folder.
3. To remove pairing, settings and logs, delete %LOCALAPPDATA%\PaceLaps\Agent.
4. Revoke the agent in race control (Agents).
