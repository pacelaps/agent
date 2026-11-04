using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.ConsoleHost.Poc;
using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.ConsoleHost.Spikes.Guided;
using Microsoft.Extensions.Logging;

namespace PaceLaps.Agent.ConsoleHost;

internal static class Program
{
    private const string Usage = """
        pacelaps-agent-cli: the agent console (run, pair, config) and the Phase 0 spike runners. The operator's
        program is the tray app, pacelaps-agent.exe.

        Commands:
          pair    --server URL [--again]       Pair this PC with the web service (device code, approve it in the browser).
          config  [--init] [--file PATH]       Show the settings file (server, league, macros, local failsafe); --init writes one.
          run     [--server URL] [--realtime-key KEY] [--macros "14=!pitclose;15=!pitopen"] [--app-ini PATH] [--league ID]
                  [--harmless !help] [--no-replay] [--lead 1500] [--max-chat 250] [--max-pace-laps 20] [--no-chat-check] [--typed-way posted|focus]
                  [--stub] [--observer] [--stub-league ID] [--stub-offset-ms 0]
                                               The agent. Settings come from the settings file (config); these options
                                               override them. With a server: the web service (after pair); without one,
                                               or with --stub: the proof of concept's stub server (--observer, --stub-*).
                                               --league names the league in a hosted session (no league id).
          probe   [--vars]                     Check the setup: sim, session, admin flag, window mode, elevation.
          spike1  --slot N [--count 20] [--interval 3] [--start-delay 10] [--macro-text TEXT]
                                               Trigger chat macro slot N (1-15) with another window in front.
          spike2  --mode borderless|fullscreen [--count 50] [--interval 2] [--start-delay 10] [--prefix /rc]
                                               Send numbered messages through the typed channel.
          spike3  --car NUM [--car2 NUM] [--steps FILE]
                                               Guided admin command test (spike3-steps.json).
          spike4  [--minutes 15]               Incident count latency; you drive and cause incidents.
          spike5  [--minutes 5]                Per-car field update rates and a 2 Hz recording (docs/recordings).
          spike6  [--back 60] [--car N]        Replay jumps while live, and back to live; --car: camera on car N. Do not drive.
          spike7  [--lengths 60,100,...]       Maximum chat message length with ruler texts.
          spike8                               Operator name and admin flag in this session.
          spike9                               Caution extension at one lap to go (guided, spike9-steps.json).
          spike10 --marker TEXT [--dir DIR]    Find the macro configuration on disk (read-only).
          spike11                              Session identity ids (league, season, session, sub-session).
          spike12                              Harmless command for the typed self-test (guided).
          spike13                              Caution without the AI pace car (guided).
          spike14 --car YOURCAR [--auto]       Crew Chief and spotter reactions (guided; --auto while you drive).
          spike15 [--reps 2] [--settle 400]    Chat Enter reliability and chat readback (after the acceptance run).
          calibrate-record [--laps 5] [--out FILE]
                                               Race control's lap start calibration (development): records every car's
                                               lap distance at 60 Hz until each car on track has driven N clean laps,
                                               then python -m track_capture calibrate FILE in the web service.
          session-record [--minutes 10] [--out FILE]
                                               Race control's header and weather card (development): records the
                                               session's laps and time (total, remaining) and the weather once a second.
          classes-record [--minutes 5] [--out FILE]
                                               Class names and colours from iRacing (development): records each car
                                               class's id, short name and colour as the session info carries them.
          results-record [--minutes 30] [--out FILE]
                                               Race control's results (development): records iRacing's results on every
                                               session info change and the cars' laps and positions once a second, through
                                               the finish and the cool-down.
          lap-times-record [--laps 5] [--out FILE]
                                               Race control's lap times card (development): records every car's lap
                                               distance, lap count, pit road and iRacing's last and best lap times at
                                               60 Hz, with the layout's sector starts, until each car on track has
                                               completed N laps.
          spike16 --part monitor [--minutes 10]
                                               Watch passively while another program sends to iRacing: front window,
                                               injected keys and mouse, clipboard, SDK broadcasts, app.ini.
          spike16 --part send [--count 3] [--interval 4] [--start-delay 10] [--variants A,B,C,D] [--chat-open-delay 150]
                  [--settle 400] [--no-readback]
                                               Chat lines without bringing iRacing to the front (window messages), and
                                               the focus-and-paste way for comparison.
          spike16 --part soak [--lines 25] [--interval 2] [--car NUM] [--reps 3] [--driving-lines 10] [--delivery post|send]
                                               The window-message way at length: 50 numbered lines, !black/!clear checked
                                               in telemetry, special characters, a caution with a message, lines while driving.
          spike16 --part driving [--parked-reps 3] [--driving-reps 2] [--interval 3]
                                               In the car: the other tool timed by the monitor, then five forms (waits, posted
                                               or sent) parked in the pit stall and while driving.
          guided  --spike N --steps FILE [--car NUM] [--car2 NUM] [--auto]
                                               Any steps file. --auto runs the steps without prompts.

        Guided options: --no-stay-in-sim keeps this window in front after each command.
        Typed channel options (spike2, spike7, guided), in milliseconds:
          --chat-open-delay 150  --key-delay 0  --after-enter-delay 100  --focus-timeout 1000

        Common:
          --log-dir DIR    Where spike logs go (default: docs/spike-logs in the repository).

        Press Ctrl+C to abort a run; the log keeps everything up to that point.
        """;

    public static async Task<int> Main(string[] args)
    {
        CliArgs cli;
        try
        {
            cli = CliArgs.Parse(args);
        }
        catch (CliException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (cli.Command is "help" or "-h" or "/?")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using var loggerFactory = AgentLogging.Create();
        var logger = loggerFactory.CreateLogger("Program");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        logger.LogInformation("Start {Command} {Version}", cli.Command, SpikeEnvironment.AgentVersion);
        if (cli.Command == "config")
        {
            try
            {
                return ConfigCommand.Run(cli);
            }
            catch (Exception e) when (e is CliException or SettingsFileException)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
        }

        if (cli.Command == "pair")
        {
            try
            {
                return await PairCommand.RunAsync(cli, cts.Token);
            }
            catch (CliException e)
            {
                Console.Error.WriteLine(e.Message);
                return 2;
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or InvalidDataException)
            {
                Console.Error.WriteLine($"Pairing failed: {e.Message}");
                return 1;
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("Aborted.");
                return 130;
            }
        }

        using var env = new SpikeEnvironment(cli, loggerFactory, cts.Token);
        try
        {
            return cli.Command switch
            {
                "probe" => await ProbeCommand.RunAsync(env),
                "spike1" => await Spike1Macro.RunAsync(env),
                "spike2" => await Spike2TypedChat.RunAsync(env),
                "spike3" => await GuidedRunner.RunAsync(env, 3, "spike3-steps.json"),
                "guided" => await GuidedRunner.RunAsync(env, 0, null),
                "spike4" => await Spike4Incidents.RunAsync(env),
                "spike5" => await Spike5Fields.RunAsync(env),
                "spike6" => await Spike6Replay.RunAsync(env),
                "spike7" => await Spike7Length.RunAsync(env),
                "spike8" => await Spike8And11Identity.RunSpike8Async(env),
                "spike9" => await GuidedRunner.RunAsync(env, 9, "spike9-steps.json"),
                "spike10" => await Spike10MacroConfig.RunAsync(env),
                "spike11" => await Spike8And11Identity.RunSpike11Async(env),
                "spike12" => await GuidedRunner.RunAsync(env, 12, "spike12-steps.json"),
                "spike13" => await GuidedRunner.RunAsync(env, 13, "spike13-steps.json"),
                "spike14" => await GuidedRunner.RunAsync(env, 14, "spike14-steps.json"),
                "spike15" => await Spike15ChatEnter.RunAsync(env),
                "spike16" => await Spike16Background.RunAsync(env),
                "calibrate-record" => await CalibrateRecord.RunAsync(env),
                "lap-times-record" => await LapTimesRecord.RunAsync(env),
                "session-record" => await SessionRecord.RunAsync(env),
                "results-record" => await ResultsRecord.RunAsync(env),
                "classes-record" => await ClassesRecord.RunAsync(env),
                "run" => await RunCommand.RunAsync(env),
                _ => throw new CliException($"unknown command '{cli.Command}'"),
            };
        }
        catch (CliException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (SettingsFileException e)
        {
            Console.Error.WriteLine(e.Message);
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Aborted.");
            logger.LogWarning("{Command} aborted by the operator", cli.Command);
            return 130;
        }
        catch (InvalidDataException e)
        {
            Console.Error.WriteLine("Steps file: " + e.Message);
            return 2;
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Command} failed", cli.Command);
            Console.Error.WriteLine($"Failed: {e.Message} (details in {AgentLogging.LogDirectory})");
            return 1;
        }
    }
}
