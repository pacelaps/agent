using System.Globalization;
using IRSDKSharper;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Records the car classes as iRacing's session info carries them, for class names and colours from iRacing,
/// before the contract changes: whether <c>CarClassShortName</c> is filled in, how
/// long it is, whose name it is (iRacing's, or the one the session admin gave the class), and what <c>CarClassColor</c>
/// holds. A development tool, run in the user's own session; nothing here talks to the server, and no names of people
/// are written (cars by index, number and class).
/// </summary>
/// <remarks>
/// JSON Lines: a header; then <c>classes</c> whenever the session info's class data changes: the event (type,
/// category, league id, number of classes and car types), each car's class fields and car names, and per class id
/// the names and colours its cars carry (more than one would mean the fields are per car, not per class).
/// </remarks>
internal static class ClassesRecord
{
    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var minutes = env.Args.GetInt("minutes", 5, 1, 60);
        var dir = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "recordings");
        Directory.CreateDirectory(dir);
        var path = env.Args.Get("out") is { } file
            ? Path.GetFullPath(file)
            : Path.Combine(dir, $"classes-{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z.jsonl");

        var gate = new object();
        var changes = 0;
        string? last = null;
        await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));

        void OnSessionInfo(IRacingSdkSessionInfo info)
        {
            var drivers = info.DriverInfo?.Drivers ?? [];
            var weekend = info.WeekendInfo;
            var cars = drivers.Select(d => new
            {
                idx = d.CarIdx,
                num = d.CarNumber,
                classId = d.CarClassID,
                classShortName = d.CarClassShortName,
                classColor = Raw(d.CarClassColor),
                carId = d.CarID,
                carScreenName = d.CarScreenName,
                carScreenNameShort = d.CarScreenNameShort,
                pace = d.CarIsPaceCar == 1,
                spectator = d.IsSpectator == 1,
            }).ToArray();
            var classes = cars.Where(c => !c.pace && !c.spectator).GroupBy(c => c.classId).OrderBy(g => g.Key).Select(g => new
            {
                id = g.Key,
                names = g.Select(c => c.classShortName).Distinct().ToArray(),
                colours = g.Select(c => c.classColor).Distinct().ToArray(),
                cars = g.Count(),
                carModels = g.Select(c => c.carScreenName).Distinct().ToArray(),
            }).ToArray();
            var line = Json(new
            {
                type = "classes",
                t = env.Sim.Latest is { } f ? Math.Round(f.SessionTime, 2) : (double?)null,
                weekend = new
                {
                    eventType = weekend?.EventType,
                    category = weekend?.Category,
                    leagueId = weekend?.LeagueID,
                    numCarClasses = weekend?.NumCarClasses,
                    numCarTypes = weekend?.NumCarTypes,
                },
                classes,
                cars,
            });
            // The time changes with every session info; compare without it.
            var key = line[(line.IndexOf(",\"weekend\"", StringComparison.Ordinal))..];
            lock (gate)
            {
                if (key == last)
                {
                    return;
                }

                last = key;
                changes++;
                writer.WriteLine(line);
                writer.Flush();
                Console.WriteLine($"  Classes ({weekend?.EventType}, league {weekend?.LeagueID}):");
                foreach (var c in classes)
                {
                    Console.WriteLine($"    {c.id}: name {string.Join(" / ", c.names.Select(n => $"\"{n}\""))}, colour {string.Join(" / ", c.colours)}, {c.cars} cars ({string.Join(", ", c.carModels)})");
                }
            }
        }

        // Before connecting: the first session info arrives with the connection.
        env.Sim.SessionInfoCaptured += OnSessionInfo;
        try
        {
            if (!await env.ConnectAsync(["SessionNum"], updateInterval: 60))
            {
                return 1;
            }

            var session = env.Sim.Session!;
            lock (gate)
            {
                writer.WriteLine(Json(new
                {
                    type = "header",
                    format = 1,
                    capturedAt = env.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    iracingBuild = session.BuildVersion,
                    track = new { id = session.TrackId, name = session.TrackDisplayName, config = session.TrackConfigName },
                    session = new { num = session.CurrentSessionNum, type = session.CurrentSessionType },
                }));
                writer.Flush();
            }

            Prompt.Heading($"Recording the car classes for up to {minutes} min. Type q and Enter to stop.");
            Console.WriteLine("Join the session (cars on the grid are enough); the classes print when iRacing reports them.");
            Console.WriteLine($"File: {path}");
            var end = env.Clock.Elapsed + TimeSpan.FromMinutes(minutes);
            while (env.Clock.Elapsed < end)
            {
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
            env.Sim.SessionInfoCaptured -= OnSessionInfo;
        }

        Console.WriteLine($"Done: {changes} class changes. File: {path}");
        return changes > 0 ? 0 : 1;
    }

    /// <summary>The colour as iRacing's session info gives it, with its type, since the format is part of the check.</summary>
    private static string? Raw(object? value) => value is null ? null : $"{value} ({value.GetType().Name})";

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, SpikeLog.JsonOptions);
}
