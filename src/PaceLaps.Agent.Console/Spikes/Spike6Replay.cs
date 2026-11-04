using System.Globalization;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Spike 6: can the replay jump to a session time while the sim is live? Jumps the local replay back in the
/// current session and to an earlier session, returns to live, and records the replay variables. It also checks
/// whether the agent's car telemetry follows the replay (which would corrupt snapshots while replaying). With
/// <c>--car N</c> the camera follows that car in each replay (race control's incident review), and CamCarIdx is logged.
/// </summary>
internal static class Spike6Replay
{
    public static readonly string[] Vars =
    [
        "IsReplayPlaying", "ReplayFrameNum", "ReplayFrameNumEnd", "ReplayPlaySpeed", "ReplaySessionNum", "ReplaySessionTime",
        "SessionNum", "SessionTime", "CarIdxLapDistPct", "CarIdxLapCompleted", "CamCarIdx", "CamGroupNumber",
    ];

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        // 180 s back crosses a lap line for every car, so laps completed show whether car data follows the replay.
        var back = env.Args.GetSeconds("back", TimeSpan.FromSeconds(180), 3600);
        var car = env.Args.Get("car");
        if (!await env.ConnectAsync(Vars))
        {
            return 1;
        }

        using var log = env.CreateLog(6);
        log.Write("header", env.Header(6, log.RunId, new { backSeconds = back.TotalSeconds, car }));
        Console.WriteLine($"Log: {log.Path}");
        Prompt.Heading("Spike 6: replay jumps. Stay in the garage or spectate; do not drive during this spike.");

        var s = env.Sim.Session!;
        var attempts = new List<(string Id, int SessionNum, double Time)>();
        var now = env.Sim.Latest!.SessionTime;
        attempts.Add(("back_in_session", s.CurrentSessionNum, Math.Max(0, now - back.TotalSeconds)));
        if (s.CurrentSessionNum > 0)
        {
            attempts.Add(("previous_session", s.CurrentSessionNum - 1, 30));
        }

        for (var i = 1; i <= 3; i++)
        {
            attempts.Add(($"quick_{i}", s.CurrentSessionNum, Math.Max(0, env.Sim.Latest!.SessionTime - 20 - 10 * i)));
        }

        int jumped = 0, returned = 0;
        foreach (var (id, sessionNum, time) in attempts)
        {
            Prompt.Heading($"{id}: jump to session {sessionNum}, {time:F0} s, then back to live");
            if (Prompt.Ask("  [Enter] go, [s] skip:").Equals("s", StringComparison.OrdinalIgnoreCase))
            {
                log.Write("attempt", new { id, skipped = true });
                continue;
            }

            var before = Sample(env.Sim.Latest!);
            env.Sim.ReplayJump(sessionNum, time);
            if (car is not null)
            {
                var group = env.Sim.Latest!.Get("CamGroupNumber") is { } g ? Convert.ToInt32(g, CultureInfo.InvariantCulture) : 0;
                env.Sim.CameraOnCar(car, group);
            }

            var during = await WatchAsync(env, log, id, "replay", TimeSpan.FromSeconds(8));
            var sawReplay = Prompt.YesNo("  Did the screen jump to the replay?");
            bool? onCar = car is null ? null : Prompt.YesNo($"  Is the camera on car #{car}?");

            env.Sim.ReplayReturnToLive();
            var after = await WatchAsync(env, log, id, "live", TimeSpan.FromSeconds(8));
            var backLive = Prompt.YesNo("  Is the screen back to live?");

            var assessment = Assess(before, during, after);
            jumped += sawReplay ? 1 : 0;
            returned += backLive ? 1 : 0;
            log.Write("attempt", new { id, sessionNum, time, before, during, after, assessment, sawReplay, backLive, car, onCar });
            Console.WriteLine($"  telemetry: {assessment}");
        }

        var notes = Prompt.Ask("Notes (blank for none):");
        var tried = attempts.Count;
        var result = jumped == tried && returned == tried ? "pass" : jumped == 0 ? "fail" : "partial";
        log.Write("summary", new { result, tried, jumped, returned, notes });
        Console.WriteLine($"Result: {result}. Jumped {jumped}/{tried}, back to live {returned}/{tried}. Log: {log.Path}");
        return 0;
    }

    /// <summary>The replay and timing values at one moment, plus the first car's lap distance as a live/replay hint.</summary>
    internal sealed record ReplaySample(bool? IsReplayPlaying, int? FrameNum, int? FrameNumEnd, int? ReplaySessionNum,
        double? ReplaySessionTime, double SessionTime, double? LeadLapDist, int? SumLapsCompleted = null, int? CarsInWorld = null,
        int? CamCarIdx = null);

    public static ReplaySample Sample(TelemetryFrame f) => new(
        f.Get("IsReplayPlaying") as bool?,
        f.Get("ReplayFrameNum") is { } a ? Convert.ToInt32(a, CultureInfo.InvariantCulture) : null,
        f.Get("ReplayFrameNumEnd") is { } b ? Convert.ToInt32(b, CultureInfo.InvariantCulture) : null,
        f.Get("ReplaySessionNum") is { } c ? Convert.ToInt32(c, CultureInfo.InvariantCulture) : null,
        f.Get("ReplaySessionTime") is { } d ? Math.Round(Convert.ToDouble(d, CultureInfo.InvariantCulture), 2) : null,
        Math.Round(f.SessionTime, 2),
        f.Get("CarIdxLapDistPct", 0) is { } e ? Math.Round(Convert.ToDouble(e, CultureInfo.InvariantCulture), 4) : null,
        f.Values.TryGetValue("CarIdxLapCompleted", out var laps) ? laps.Sum(l => Math.Max(0, Convert.ToInt32(l, CultureInfo.InvariantCulture))) : null,
        f.Values.TryGetValue("CarIdxLapDistPct", out var dist) ? dist.Count(d => Convert.ToDouble(d, CultureInfo.InvariantCulture) >= 0) : null,
        f.Get("CamCarIdx") is { } cam ? Convert.ToInt32(cam, CultureInfo.InvariantCulture) : null);

    /// <summary>
    /// Reads the three samples: did the replay position move away from the live end, did it come back, and did the
    /// session time (which the agent uses for live data) jump back with the replay.
    /// </summary>
    public static string Assess(ReplaySample before, ReplaySample during, ReplaySample after)
    {
        var parts = new List<string>();
        parts.Add(during.FrameNumEnd is > 0 ? $"in replay ({during.FrameNumEnd} frames behind live)" : "replay position did not move away from live");
        parts.Add(after.FrameNumEnd is 0 or null ? "back at live end" : $"still {after.FrameNumEnd} frames behind live");
        parts.Add(during.SessionTime < before.SessionTime - 1
            ? "SessionTime jumped back: telemetry follows the replay"
            : "SessionTime kept running");

        // Laps completed summed over all cars only grows while live; a drop means the car arrays show the replay.
        if (before.SumLapsCompleted is { } b && during.SumLapsCompleted is { } d)
        {
            parts.Add(d < b
                ? $"car laps went back ({b} -> {d}): per-car telemetry follows the replay"
                : $"car laps kept counting ({b} -> {d}): per-car telemetry stays live");
        }

        return string.Join("; ", parts);
    }

    private static async Task<ReplaySample> WatchAsync(SpikeEnvironment env, SpikeLog log, string id, string phase, TimeSpan duration)
    {
        var until = env.Clock.Elapsed + duration;
        var next = TimeSpan.Zero;
        ReplaySample last = Sample(env.Sim.Latest!);
        while (env.Clock.Elapsed < until)
        {
            await env.Clock.Delay(TimeSpan.FromMilliseconds(100), env.Cancellation);
            if (env.Clock.Elapsed >= next)
            {
                next = env.Clock.Elapsed + TimeSpan.FromSeconds(1);
                last = Sample(env.Sim.Latest!);
                log.Write("sample", new { id, phase, sample = last });
            }
        }

        return Sample(env.Sim.Latest!);
    }
}
