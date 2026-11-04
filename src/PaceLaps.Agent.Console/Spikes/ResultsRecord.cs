using System.Globalization;
using IRSDKSharper;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Records iRacing's results as the session info carries them, for race control's results,
/// before the contract changes: when <c>ResultsPositions</c> update (each
/// lap, at the finish, after the cool-down), whether <c>Time</c> is the race time, how lapped and retired cars appear,
/// and when <c>ResultsOfficial</c> turns on. A development tool, run in the user's own session; nothing here talks to
/// the server, and no names are written (cars by index, number and class).
/// </summary>
/// <remarks>
/// JSON Lines: a header; <c>cars</c> (index, number, class, pace car, spectator) whenever the list changes;
/// <c>info</c> with every session's results fields (all of iRacing's per-session values except the names) whenever
/// they change; <c>s</c> once a second with the session state and flags and each car's laps completed and live
/// positions, to compare with the results.
/// </remarks>
internal static class ResultsRecord
{
    private static readonly string[] Vars =
        ["SessionNum", "SessionState", "SessionFlags", "CarIdxLapCompleted", "CarIdxPosition", "CarIdxClassPosition", "CarIdxOnPitRoad"];

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var minutes = env.Args.GetInt("minutes", 30, 1, 240);
        var dir = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "recordings");
        Directory.CreateDirectory(dir);
        var path = env.Args.Get("out") is { } file
            ? Path.GetFullPath(file)
            : Path.Combine(dir, $"results-{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z.jsonl");

        var gate = new object();
        var infos = 0;
        var lines = 0;
        string? lastSessions = null;
        string? lastCars = null;
        var lastSecond = double.NegativeInfinity;
        await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));

        void Write(object line)
        {
            lock (gate)
            {
                lines++;
                writer.WriteLine(Json(line));
                writer.Flush();
            }
        }

        void OnSessionInfo(IRacingSdkSessionInfo info)
        {
            var t = env.Sim.Latest is { } f ? Math.Round(f.SessionTime, 2) : (double?)null;
            var cars = Json(info.DriverInfo?.Drivers?.Select(d => new { idx = d.CarIdx, num = d.CarNumber, classId = d.CarClassID, pace = d.CarIsPaceCar == 1, spectator = d.IsSpectator == 1 }) ?? []);
            // Every per-session value iRacing sends (results, fastest lap, official), as it names them.
            var sessions = Json(info.SessionInfo?.Sessions ?? []);
            lock (gate)
            {
                if (cars != lastCars)
                {
                    lastCars = cars;
                    lines++;
                    writer.WriteLine($"{{\"type\":\"cars\",\"t\":{Num(t)},\"cars\":{cars}}}");
                }

                if (sessions != lastSessions)
                {
                    lastSessions = sessions;
                    infos++;
                    lines++;
                    writer.WriteLine($"{{\"type\":\"info\",\"t\":{Num(t)},\"current\":{info.SessionInfo?.CurrentSessionNum ?? -1},\"sessions\":{sessions}}}");
                }

                writer.Flush();
            }
        }

        void OnFrame(TelemetryFrame f)
        {
            if (f.SessionTime - lastSecond < 1 && f.SessionTime >= lastSecond)
            {
                return;
            }

            lastSecond = f.SessionTime;
            var s = env.Sim.Session;
            var cars = s is null ? [] : s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar).Select(c => new object?[]
            {
                c.CarIdx,
                Int(f.Get("CarIdxLapCompleted", c.CarIdx)),
                Int(f.Get("CarIdxPosition", c.CarIdx)),
                Int(f.Get("CarIdxClassPosition", c.CarIdx)),
                f.Get("CarIdxOnPitRoad", c.CarIdx) as bool?,
            }).ToArray();
            Write(new { type = "s", t = Math.Round(f.SessionTime, 2), num = Int(f.Get("SessionNum")), state = Int(f.Get("SessionState")), flags = Int(f.Get("SessionFlags")), cars });
        }

        // Before connecting: the first session info arrives with the connection.
        env.Sim.SessionInfoCaptured += OnSessionInfo;
        try
        {
            if (!await env.ConnectAsync(Vars, updateInterval: 6))
            {
                return 1;
            }

            var session = env.Sim.Session!;
            Write(new
            {
                type = "header",
                format = 1,
                capturedAt = env.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                iracingBuild = session.BuildVersion,
                track = new { id = session.TrackId, name = session.TrackDisplayName, config = session.TrackConfigName },
                session = new { num = session.CurrentSessionNum, type = session.CurrentSessionType, laps = session.CurrentSessionLaps, time = session.CurrentSessionTime },
                sessions = session.Sessions.Select(x => new { num = x.Num, type = x.Type }),
            });

            Prompt.Heading($"Recording iRacing's results for up to {minutes} min. Type q and Enter to stop.");
            Console.WriteLine("Run a short AI race (a few laps). If you can, retire or tow once. Let it run past the checkered flag");
            Console.WriteLine("and through the cool-down until the results are official, then stop with q.");
            Console.WriteLine($"File: {path}");
            env.Sim.FrameCaptured += OnFrame;
            var end = env.Clock.Elapsed + TimeSpan.FromMinutes(minutes);
            var nextLine = env.Clock.Elapsed + TimeSpan.FromSeconds(5);
            while (env.Clock.Elapsed < end)
            {
                if (env.Clock.Elapsed >= nextLine)
                {
                    nextLine += TimeSpan.FromSeconds(30);
                    lock (gate)
                    {
                        Console.WriteLine($"  {lines} lines, {infos} results changes");
                    }
                }

                if (Prompt.KeyIfAvailable() is { Key: ConsoleKey.Q })
                {
                    Console.WriteLine("Stopped.");
                    break;
                }

                await env.Clock.Delay(TimeSpan.FromMilliseconds(200), env.Cancellation);
            }
        }
        finally
        {
            env.Sim.FrameCaptured -= OnFrame;
            env.Sim.SessionInfoCaptured -= OnSessionInfo;
        }

        Console.WriteLine($"Done: {lines} lines, {infos} results changes. File: {path}");
        return infos > 0 ? 0 : 1;
    }

    private static int? Int(object? value) => value is null ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static string Num(double? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "null";

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, SpikeLog.JsonOptions);
}
