using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>Checks the setup before a spike: sim connected, session, admin flag, window mode, elevation, layout.</summary>
internal static class ProbeCommand
{
    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        if (!await env.ConnectAsync(["SessionFlags", "SessionState", "PaceMode", "PitsOpen"]))
        {
            return 1;
        }

        var s = env.Sim.Session!;
        var frame = env.Sim.Latest!;
        var sim = env.Focus.FindSimWindow();
        var window = env.Focus.DescribeSimWindow();
        var me = s.CarByIdx(s.DriverCarIdx);

        Prompt.Heading("Probe");
        Console.WriteLine($"  iRacing build        {s.BuildVersion} ({s.SimMode})");
        Console.WriteLine($"  Session              {s.CurrentSessionNum} {s.CurrentSessionType}; all: {string.Join(", ", s.Sessions.Select(x => $"{x.Num} {x.Type}"))}");
        Console.WriteLine($"  League/sub-session   {s.LeagueId} / {s.SubSessionId}");
        Console.WriteLine($"  Cars                 {s.Cars.Count(c => !c.IsPaceCar && !c.IsSpectator)} (pace car idx {s.PaceCarIdx}): "
            + string.Join(" ", s.Cars.Where(c => !c.IsPaceCar && !c.IsSpectator).Select(c => "#" + c.CarNumber)));
        Console.WriteLine($"  You                  car idx {s.DriverCarIdx}, spectator {me?.IsSpectator.ToString() ?? "not in driver list"}, DriverIsAdmin {s.DriverIsAdmin}");
        Console.WriteLine($"  Flags                {SdkNames.SessionFlags(Convert.ToUInt32(frame.Get("SessionFlags") ?? 0u))}");
        Console.WriteLine($"  Session state        {SdkNames.SessionState(Convert.ToInt32(frame.Get("SessionState") ?? 0))}");
        Console.WriteLine($"  Pace mode            {SdkNames.PaceMode(Convert.ToInt32(frame.Get("PaceMode") ?? -1))}");
        Console.WriteLine($"  PitsOpen var         {(frame.Get("PitsOpen") is { } p ? p.ToString() : "not broadcast")}");
        Console.WriteLine($"  Sim window           {(window is null ? "not found" : $"{window.Process}, {window.ModeHint}, rect {window.Rect}, monitor {window.Monitor}")}");
        Console.WriteLine($"  Elevated             agent {ProcessElevation.IsCurrentProcessElevated()}, iRacing {(sim.IsNone ? "?" : ProcessElevation.DescribeProcessOfWindow(sim.Value))}");
        Console.WriteLine($"  Keyboard layout      {env.Focus.SimKeyboardLayout() & 0xFFFF:X4}");
        Console.WriteLine($"  Spike logs go to     {env.LogDirectory}");

        if (env.Args.Has("vars"))
        {
            Console.WriteLine();
            Console.WriteLine("  Telemetry variables: " + string.Join(", ", env.Sim.AvailableVars.Order()));
        }

        return 0;
    }
}
