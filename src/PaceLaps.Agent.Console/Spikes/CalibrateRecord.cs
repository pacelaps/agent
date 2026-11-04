using System.Globalization;
using PaceLaps.Agent.Core.Calibration;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Records the cars' lap distance at every sim frame for race control's lap start calibration:
/// a development tool, run in the user's own AI session on one
/// layout, until every car still on track has driven the given number of clean laps. The matching happens in the
/// web service's track capture tool (python -m track_capture calibrate FILE); nothing here talks to the server.
/// </summary>
/// <remarks>
/// The file is JSON Lines: a header (the track from the session info), one line per frame
/// <c>{"type":"f","t":sessionTime,"cars":[[number,lapsCompleted,lapDistPct,onPitRoad,surface],...]}</c>, a line
/// <c>{"type":"lap","car":number,"lap":lapsCompleted}</c> whenever a car completes a clean lap, and at the end the clean
/// laps per car, which the matching uses. The matching can follow the file while it grows and end the recording early
/// once it is sure (python -m track_capture calibrate --record): it creates <c>FILE.stop</c>, and the recorder stops.
/// </remarks>
internal static class CalibrateRecord
{
    private static readonly string[] Vars = ["CarIdxLapDistPct", "CarIdxLapCompleted", "CarIdxOnPitRoad", "CarIdxTrackSurface"];

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

        var dir = env.Args.Get("out") is { } given ? Path.GetDirectoryName(Path.GetFullPath(given))! : Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "recordings");
        Directory.CreateDirectory(dir);
        var path = env.Args.Get("out") is { } file
            ? Path.GetFullPath(file)
            : Path.Combine(dir, $"calibration-{track.Id}-{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z.jsonl");

        var counter = new CleanLaps();
        var gate = new object();
        var frames = 0;
        await using var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        writer.WriteLine(Json(new
        {
            type = "header",
            format = 1,
            capturedAt = env.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            iracingBuild = session.BuildVersion,
            track = new { id = track.Id, name = track.Name, config = track.Config, lengthM = track.LengthM },
            laps,
        }));

        void OnFrame(TelemetryFrame f)
        {
            var s = env.Sim.Session;
            if (s is null)
            {
                return;
            }

            var cars = new List<object[]>();
            foreach (var c in s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar))
            {
                if (f.Get("CarIdxLapDistPct", c.CarIdx) is not { } d || f.Get("CarIdxLapCompleted", c.CarIdx) is not { } l)
                {
                    continue;
                }

                var pct = Convert.ToDouble(d, CultureInfo.InvariantCulture);
                var completed = Convert.ToInt32(l, CultureInfo.InvariantCulture);
                var pit = f.Get("CarIdxOnPitRoad", c.CarIdx) is true;
                var surface = f.Get("CarIdxTrackSurface", c.CarIdx) is { } sv ? Convert.ToInt32(sv, CultureInfo.InvariantCulture) : -1;
                var inWorld = pct >= 0 && surface != SimValues.SurfaceNotInWorld;
                int? clean;
                lock (gate)
                {
                    clean = counter.Observe(c.CarNumber, completed, pit, inWorld);
                }

                if (clean is { } lap)
                {
                    lock (gate)
                    {
                        writer.WriteLine(Json(new { type = "lap", car = c.CarNumber, lap }));
                        writer.Flush(); // the matching follows the file and runs at every clean lap
                    }
                }

                if (inWorld)
                {
                    cars.Add([c.CarNumber, completed, Math.Round(pct, 6), pit ? 1 : 0, surface]);
                }
            }

            lock (gate)
            {
                frames++;
                writer.WriteLine(Json(new { type = "f", t = Math.Round(f.SessionTime, 4), cars }));
            }
        }

        Prompt.Heading($"Recording {track.Name} – {track.Config} (Track No. {track.Id}) until every car on track has {laps} clean laps. "
            + "Laps start at a lap-count change; a lap with time on pit road does not count. Type q and Enter to stop early.");
        Console.WriteLine($"File: {path}");
        var stopFile = path + ".stop";
        File.Delete(stopFile);
        env.Sim.FrameCaptured += OnFrame;
        try
        {
            var nextLine = env.Clock.Elapsed;
            while (true)
            {
                lock (gate)
                {
                    if (counter.Finished(laps))
                    {
                        break;
                    }
                }

                if (env.Clock.Elapsed >= nextLine)
                {
                    nextLine += TimeSpan.FromSeconds(10);
                    IReadOnlyDictionary<string, int> progress;
                    lock (gate)
                    {
                        progress = counter.Progress;
                    }

                    Console.WriteLine(progress.Count == 0
                        ? "  waiting for a car on track..."
                        : "  clean laps: " + string.Join(", ", progress.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"#{p.Key} {p.Value}/{laps}")));
                }

                // The matching following the file is sure already (calibrate --record): it asks to stop.
                if (File.Exists(stopFile))
                {
                    File.Delete(stopFile);
                    Console.WriteLine("Stopped: the matching is sure already.");
                    break;
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

        IReadOnlyDictionary<string, IReadOnlyList<int>> done;
        lock (gate)
        {
            done = counter.Done;
            writer.WriteLine(Json(new { type = "laps", clean = done }));
        }

        var total = done.Values.Sum(v => v.Count);
        Console.WriteLine($"Done: {frames} frames, {total} clean laps ({string.Join(", ", done.Select(d => $"#{d.Key} {d.Value.Count}"))}).");
        Console.WriteLine($"File: {path}");
        Console.WriteLine("Next, in webservice/tools/track-capture: .\\.venv\\Scripts\\python -m track_capture calibrate \"" + path + "\"");
        return total > 0 ? 0 : 1;
    }

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, SpikeLog.JsonOptions);
}
