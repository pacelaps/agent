using System.Globalization;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Records what race control's lap times card needs to know about iRacing's timing data,
/// before the contract is changed: every car's lap distance, lap
/// count, pit road and surface at every sim frame, iRacing's own last and best lap times, and the layout's sector
/// starts from the session info. A development tool, run in the user's own AI session; nothing here talks to the server.
/// </summary>
/// <remarks>
/// The file is JSON Lines: a header (the track, the sector starts from SplitTimeInfo, the session and the cars by
/// number and class, no names), one line per frame
/// <c>{"type":"f","t":sessionTime,"cars":[[number,lapsCompleted,lapDistPct,onPitRoad,surface,lastLapTime,bestLapTime,bestLapNum],...]}</c>
/// and at the end the laps each car completed while recording.
/// </remarks>
internal static class LapTimesRecord
{
    private static readonly string[] Vars =
    [
        "SessionNum", "CarIdxLapDistPct", "CarIdxLapCompleted", "CarIdxOnPitRoad", "CarIdxTrackSurface",
        "CarIdxLastLapTime", "CarIdxBestLapTime", "CarIdxBestLapNum",
    ];

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var laps = env.Args.GetInt("laps", 5, 1, 50);
        if (!await env.ConnectAsync(Vars, updateInterval: 1))
        {
            return 1;
        }

        var session = env.Sim.Session!;
        var track = SimTrack.From(session.TrackId, session.TrackDisplayName, session.TrackConfigName, session.TrackLength);
        if (track is null)
        {
            Console.Error.WriteLine("The session info names no track: is the sim in a session on a layout?");
            return 1;
        }

        var dir = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "recordings");
        Directory.CreateDirectory(dir);
        var path = env.Args.Get("out") is { } file
            ? Path.GetFullPath(file)
            : Path.Combine(dir, $"lap-times-{track.Id}-{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z.jsonl");

        var gate = new object();
        var frames = 0;
        var first = new Dictionary<string, int>(StringComparer.Ordinal); // laps completed when first seen
        var latest = new Dictionary<string, (int Laps, bool OnTrack)>(StringComparer.Ordinal);
        await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        writer.WriteLine(Json(new
        {
            type = "header",
            format = 1,
            capturedAt = env.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            iracingBuild = session.BuildVersion,
            track = new { id = track.Id, name = track.Name, config = track.Config, lengthM = track.LengthM },
            sectorStarts = session.SectorStarts,
            session = new { num = session.CurrentSessionNum, type = session.CurrentSessionType },
            cars = session.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar).Select(c => new { num = c.CarNumber, classId = c.ClassId }),
            laps,
        }));

        void OnFrame(TelemetryFrame f)
        {
            var s = env.Sim.Session;
            if (s is null)
            {
                return;
            }

            var cars = new List<object?[]>();
            foreach (var c in s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar))
            {
                if (f.Get("CarIdxLapDistPct", c.CarIdx) is not { } d || f.Get("CarIdxLapCompleted", c.CarIdx) is not { } l)
                {
                    continue;
                }

                var pct = Convert.ToDouble(d, CultureInfo.InvariantCulture);
                var completed = Convert.ToInt32(l, CultureInfo.InvariantCulture);
                var surface = f.Get("CarIdxTrackSurface", c.CarIdx) is { } sv ? Convert.ToInt32(sv, CultureInfo.InvariantCulture) : -1;
                if (pct < 0 || surface == SimValues.SurfaceNotInWorld)
                {
                    lock (gate)
                    {
                        latest[c.CarNumber] = (latest.GetValueOrDefault(c.CarNumber).Laps, false);
                    }

                    continue;
                }

                cars.Add([
                    c.CarNumber, completed, Math.Round(pct, 6), f.Get("CarIdxOnPitRoad", c.CarIdx) is true ? 1 : 0, surface,
                    Seconds(f.Get("CarIdxLastLapTime", c.CarIdx)), Seconds(f.Get("CarIdxBestLapTime", c.CarIdx)),
                    f.Get("CarIdxBestLapNum", c.CarIdx) is { } n ? Convert.ToInt32(n, CultureInfo.InvariantCulture) : -1,
                ]);
                lock (gate)
                {
                    first.TryAdd(c.CarNumber, completed);
                    latest[c.CarNumber] = (completed - first[c.CarNumber], true);
                }
            }

            lock (gate)
            {
                frames++;
                writer.WriteLine(Json(new { type = "f", t = Math.Round(f.SessionTime, 4), cars }));
            }
        }

        Prompt.Heading($"Recording lap times at {track.Name} – {track.Config} (Track No. {track.Id}) until every car on track has "
            + $"completed {laps} laps. Type q and Enter to stop early.");
        Console.WriteLine(session.SectorStarts.Count > 0
            ? $"Sectors start at {string.Join(", ", session.SectorStarts.Select(x => x.ToString("0.0000", CultureInfo.InvariantCulture)))} of the lap."
            : "The session info has no sectors (SplitTimeInfo): that is a finding too; recording anyway.");
        Console.WriteLine($"File: {path}");
        env.Sim.FrameCaptured += OnFrame;
        try
        {
            var nextLine = env.Clock.Elapsed;
            while (true)
            {
                Dictionary<string, (int Laps, bool OnTrack)> now;
                lock (gate)
                {
                    now = new Dictionary<string, (int, bool)>(latest, StringComparer.Ordinal);
                }

                var running = now.Where(c => c.Value.OnTrack).ToList();
                if (running.Count > 0 && running.All(c => c.Value.Laps >= laps))
                {
                    break;
                }

                if (env.Clock.Elapsed >= nextLine)
                {
                    nextLine += TimeSpan.FromSeconds(10);
                    Console.WriteLine(running.Count == 0
                        ? "  waiting for a car on track..."
                        : "  laps: " + string.Join(", ", running.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"#{c.Key} {c.Value.Laps}/{laps}")));
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

        Dictionary<string, int> done;
        lock (gate)
        {
            done = latest.ToDictionary(c => c.Key, c => c.Value.Laps, StringComparer.Ordinal);
            writer.WriteLine(Json(new { type = "end", laps = done }));
        }

        Console.WriteLine($"Done: {frames} frames, laps per car: {string.Join(", ", done.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => $"#{d.Key} {d.Value}"))}.");
        Console.WriteLine($"File: {path}");
        return frames > 0 ? 0 : 1;
    }

    /// <summary>A lap time in seconds rounded to 0.1 ms; null where iRacing has none (negative).</summary>
    private static double? Seconds(object? value) =>
        value is null || Convert.ToDouble(value, CultureInfo.InvariantCulture) is var s && s < 0
            ? null
            : Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture), 4);

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, SpikeLog.JsonOptions);
}
