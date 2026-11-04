using System.Globalization;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Records iRacing's session and weather values once a second, for race control's header and weather card,
/// before the contract changes: the laps and time total
/// and remaining, the leader's laps, and the weather. A development tool, run in the user's own session; nothing here
/// talks to the server.
/// </summary>
/// <remarks>
/// JSON Lines: a header (track, session, the session info's lap and time limits, which variables the sim offers), then
/// <c>{"type":"s","t":sessionTime,"v":{name: value, ...},"leaderLaps":n}</c> every second.
/// </remarks>
internal static class SessionRecord
{
    private static readonly string[] Vars =
    [
        "SessionNum", "SessionState", "SessionFlags",
        "SessionLapsTotal", "SessionLapsRemain", "SessionLapsRemainEx", "SessionTimeTotal", "SessionTimeRemain",
        "AirTemp", "TrackTemp", "TrackTempCrew", "TrackWetness", "Precipitation", "WeatherDeclaredWet", "Skies",
        "WindVel", "WindDir", "RelativeHumidity", "FogLevel", "AirPressure", "AirDensity", "SolarAltitude",
        "CarIdxLapCompleted", "CarIdxPosition",
    ];

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var minutes = env.Args.GetInt("minutes", 10, 1, 120);
        if (!await env.ConnectAsync(Vars, updateInterval: 1))
        {
            return 1;
        }

        var session = env.Sim.Session!;
        var dir = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "recordings");
        Directory.CreateDirectory(dir);
        var path = env.Args.Get("out") is { } file
            ? Path.GetFullPath(file)
            : Path.Combine(dir, $"session-{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z.jsonl");

        var gate = new object();
        var lines = 0;
        var last = double.NegativeInfinity;
        string[] offered = [];
        await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));

        void OnFrame(TelemetryFrame f)
        {
            if (f.SessionTime - last < 1 && f.SessionTime >= last)
            {
                return;
            }

            last = f.SessionTime;
            var values = new Dictionary<string, object?>();
            foreach (var name in Vars.Where(v => !v.StartsWith("CarIdx", StringComparison.Ordinal)))
            {
                values[name] = f.Get(name, 0) is { } v ? Round(v) : null;
            }

            var s = env.Sim.Session;
            var leaderLaps = s is null ? (int?)null : s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar)
                .Select(c => f.Get("CarIdxPosition", c.CarIdx) is { } p && Convert.ToInt32(p, CultureInfo.InvariantCulture) == 1
                    && f.Get("CarIdxLapCompleted", c.CarIdx) is { } l ? Convert.ToInt32(l, CultureInfo.InvariantCulture) : (int?)null)
                .FirstOrDefault(l => l is not null);
            var maxLaps = s is null ? (int?)null : s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar)
                .Select(c => f.Get("CarIdxLapCompleted", c.CarIdx) is { } l ? Convert.ToInt32(l, CultureInfo.InvariantCulture) : -1).DefaultIfEmpty(-1).Max();
            lock (gate)
            {
                offered = [.. values.Where(v => v.Value is not null).Select(v => v.Key)];
                lines++;
                writer.WriteLine(Json(new { type = "s", t = Math.Round(f.SessionTime, 2), v = values, leaderLaps, maxLaps }));
                writer.Flush();
            }
        }

        writer.WriteLine(Json(new
        {
            type = "header",
            format = 1,
            capturedAt = env.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            iracingBuild = session.BuildVersion,
            track = new { id = session.TrackId, name = session.TrackDisplayName, config = session.TrackConfigName },
            session = new { num = session.CurrentSessionNum, type = session.CurrentSessionType, laps = session.CurrentSessionLaps, time = session.CurrentSessionTime },
            sessions = session.Sessions.Select(x => new { num = x.Num, type = x.Type }),
        }));

        Prompt.Heading($"Recording session and weather values once a second for {minutes} min. Type q and Enter to stop early.");
        Console.WriteLine($"Session {session.CurrentSessionNum} ({session.CurrentSessionType}): laps {session.CurrentSessionLaps}, time {session.CurrentSessionTime}.");
        Console.WriteLine($"File: {path}");
        env.Sim.FrameCaptured += OnFrame;
        try
        {
            var end = env.Clock.Elapsed + TimeSpan.FromMinutes(minutes);
            var nextLine = env.Clock.Elapsed + TimeSpan.FromSeconds(5);
            while (env.Clock.Elapsed < end)
            {
                if (env.Clock.Elapsed >= nextLine)
                {
                    nextLine += TimeSpan.FromSeconds(30);
                    lock (gate)
                    {
                        Console.WriteLine($"  {lines} lines; the sim offers {offered.Length} of {Vars.Length - 2}: missing {string.Join(", ", Vars.Where(v => !v.StartsWith("CarIdx", StringComparison.Ordinal)).Except(offered))}");
                    }
                }

                if (Prompt.KeyIfAvailable() is { Key: ConsoleKey.Q })
                {
                    Console.WriteLine("Stopped early.");
                    break;
                }

                await env.Clock.Delay(TimeSpan.FromMilliseconds(200), env.Cancellation);
            }
        }
        finally
        {
            env.Sim.FrameCaptured -= OnFrame;
        }

        Console.WriteLine($"Done: {lines} lines. File: {path}");
        return lines > 0 ? 0 : 1;
    }

    private static object Round(object value) => value switch
    {
        float f => Math.Round(f, 3),
        double d => Math.Round(d, 3),
        _ => value,
    };

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, SpikeLog.JsonOptions);
}
