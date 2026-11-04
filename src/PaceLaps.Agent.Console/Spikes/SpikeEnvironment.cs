using System.Reflection;
using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;
using Microsoft.Extensions.Logging;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>Everything a spike runner needs: the sim connection, focus, keyboard, clock, log location.</summary>
internal sealed class SpikeEnvironment : IDisposable
{
    public SpikeEnvironment(CliArgs args, ILoggerFactory loggerFactory, CancellationToken cancellation)
    {
        Args = args;
        LoggerFactory = loggerFactory;
        Cancellation = cancellation;
        Sim = new SimConnection(loggerFactory.CreateLogger<SimConnection>(), Clock);
        Keyboard = new ScancodeKeyboard(Focus.SimKeyboardLayout);
        LogDirectory = args.Get("log-dir") ?? SpikeLog.DefaultDirectory();
    }

    public CliArgs Args { get; }
    public ILoggerFactory LoggerFactory { get; }
    public CancellationToken Cancellation { get; }
    public IClock Clock { get; } = new SystemClock();
    public Win32WindowFocus Focus { get; } = new();
    public ScancodeKeyboard Keyboard { get; }
    public SimConnection Sim { get; }
    public string LogDirectory { get; }

    /// <summary>The version with the short commit hash, within the contract's 40 characters.</summary>
    public static string AgentVersion =>
        Core.Agent.AgentVersion.Shorten(typeof(SpikeEnvironment).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public TypedChatOptions TypedChatOptionsFromArgs() => new()
    {
        ChatOpenDelay = Args.GetMs("chat-open-delay", new TypedChatOptions().ChatOpenDelay),
        InterKeyDelay = Args.GetMs("key-delay", TimeSpan.Zero, 1000),
        AfterEnterDelay = Args.GetMs("after-enter-delay", new TypedChatOptions().AfterEnterDelay),
        FocusTimeout = Args.GetMs("focus-timeout", new TypedChatOptions().FocusTimeout),
    };

    /// <summary>
    /// <c>--typed-way posted|focus</c> for <c>run</c>: true sends typed commands without the focus switch (spec-feedback
    /// 37), false the focus way; null leaves it to the settings file.
    /// </summary>
    public bool? TypedWithoutFocusFromArgs() =>
        Args.Has("typed-way") ? Args.GetChoice("typed-way", "posted", "focus") == "posted" : null;

    /// <summary>A sender for the spike runners: always the focus way (they measure it), no chat box check.</summary>
    public TypedChatSender CreateSender(TypedChatOptions options) =>
        new(Focus, Keyboard, Sim, Clock, options, LoggerFactory.CreateLogger<TypedChatSender>());

    /// <summary>Starts the SDK and waits for a session. Returns false (with a message) if iRacing is not there.</summary>
    /// <param name="updateInterval">Sim frames per capture: 6 is 10 Hz, 1 is every frame (60 Hz).</param>
    public async Task<bool> ConnectAsync(IEnumerable<string> telemetryVars, int updateInterval = 6)
    {
        Console.WriteLine("Waiting for iRacing (up to 60 s)...");
        Sim.Start(telemetryVars.Append("SessionTime"), updateInterval);
        if (!await Sim.WaitForSessionAsync(Clock, TimeSpan.FromSeconds(60), Cancellation))
        {
            Console.Error.WriteLine("iRacing is not running or is not in a session. Join the session, then run this again.");
            if (Sim.LastError is { } e)
            {
                Console.Error.WriteLine($"Last SDK error: {e.Message}");
            }

            return false;
        }

        var s = Sim.Session!;
        Console.WriteLine($"Connected. iRacing build {s.BuildVersion}, session {s.CurrentSessionNum} ({s.CurrentSessionType}), {RacingCars(s)} cars.");
        return true;
    }

    public object Header(int spike, string runId, object options)
    {
        var s = Sim.Session;
        var simWindow = Focus.FindSimWindow();
        var me = s?.CarByIdx(s.DriverCarIdx);
        return new
        {
            spike,
            runId,
            agentVersion = AgentVersion,
            args = Environment.GetCommandLineArgs().Skip(1).ToArray(),
            iracing = s is null ? null : new
            {
                build = s.BuildVersion,
                simMode = s.SimMode,
                sessionNum = s.CurrentSessionNum,
                sessionType = s.CurrentSessionType,
                sessions = s.Sessions.Select(x => new { num = x.Num, type = x.Type }).ToArray(),
                leagueId = s.LeagueId,
                subSessionId = s.SubSessionId,
                driverIsAdmin = s.DriverIsAdmin,
                operatorIsSpectator = me?.IsSpectator,
                racingCars = RacingCars(s),
                paceCarIdx = s.PaceCarIdx,
            },
            window = Focus.DescribeSimWindow(),
            windowModeHint = Focus.DescribeSimWindow()?.ModeHint,
            agentElevated = ProcessElevation.IsCurrentProcessElevated(),
            simElevated = simWindow.IsNone ? "sim not found" : ProcessElevation.DescribeProcessOfWindow(simWindow.Value),
            keyboardLayout = $"{Focus.SimKeyboardLayout() & 0xFFFF:X4}",
            options,
        };
    }

    /// <summary>Checks the things that silently break typing, and warns. Returns false if the run cannot work.</summary>
    public bool PreflightTyping()
    {
        var sim = Focus.FindSimWindow();
        if (sim.IsNone)
        {
            Console.Error.WriteLine("The iRacing window was not found.");
            return false;
        }

        var simElevated = ProcessElevation.DescribeProcessOfWindow(sim.Value);
        if (!ProcessElevation.IsCurrentProcessElevated() && simElevated != "no")
        {
            Prompt.Warn($"iRacing elevated: {simElevated}. This program is not elevated, so Windows would drop its keystrokes. "
                + "Run both normally (recommended) or both as administrator.");
            if (!Prompt.YesNo("Continue anyway?"))
            {
                return false;
            }
        }

        return true;
    }

    public async Task CountdownAsync(TimeSpan duration, string instruction)
    {
        Console.WriteLine(instruction);
        for (var left = (int)duration.TotalSeconds; left > 0; left--)
        {
            Console.Write($"\r  starting in {left,2} s ");
            await Clock.Delay(TimeSpan.FromSeconds(1), Cancellation);
        }

        Console.WriteLine("\r  running...         ");
    }

    public SpikeLog CreateLog(int spike) => SpikeLog.Create(LogDirectory, spike, Clock);

    public void Dispose() => Sim.Dispose();

    private static int RacingCars(SessionInfoView s) => s.Cars.Count(c => !c.IsPaceCar && !c.IsSpectator);
}
