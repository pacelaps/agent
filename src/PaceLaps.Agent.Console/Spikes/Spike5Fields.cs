using System.Globalization;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>Update statistics for one per-car field of one car.</summary>
internal sealed record FieldUpdate(string Var, int CarIdx, int Changes, double? MaxGapSeconds);

/// <summary>
/// Counts value changes per (variable, car) over every frame and the longest time without a change while the car
/// was moving on track. Fed on the SDK thread; read at the end.
/// </summary>
internal sealed class FieldUpdateStats
{
    private readonly object _gate = new();
    private readonly Dictionary<(string, int), (object? Last, TimeSpan LastChange, int Changes, TimeSpan MaxGap)> _state = [];
    private readonly string[] _vars;

    public FieldUpdateStats(IEnumerable<string> vars)
    {
        _vars = vars.ToArray();
    }

    public int Frames { get; private set; }

    /// <param name="carIdxFilter">Only these car indices are tracked (cars in the session, not the pace car).</param>
    public void Observe(TelemetryFrame f, Func<int, bool> carIdxFilter)
    {
        lock (_gate)
        {
            Frames++;
            var surfaces = f.Values.TryGetValue("CarIdxTrackSurface", out var s) ? s : null;
            foreach (var name in _vars)
            {
                if (!f.Values.TryGetValue(name, out var values))
                {
                    continue;
                }

                for (var i = 0; i < values.Length; i++)
                {
                    if (!carIdxFilter(i))
                    {
                        continue;
                    }

                    // The gap only counts while the car moves on track; in the pit stall nothing has to change.
                    var onTrack = surfaces is not null && i < surfaces.Length
                        && Convert.ToInt32(surfaces[i], CultureInfo.InvariantCulture) == SdkNames.SurfaceOnTrack;
                    var key = (name, i);
                    if (!_state.TryGetValue(key, out var st))
                    {
                        _state[key] = (values[i], f.Captured, 0, TimeSpan.Zero);
                        continue;
                    }

                    if (!Equals(st.Last, values[i]))
                    {
                        var gap = f.Captured - st.LastChange;
                        _state[key] = (values[i], f.Captured, st.Changes + 1, onTrack && gap > st.MaxGap ? gap : st.MaxGap);
                    }
                    else if (!onTrack)
                    {
                        // Restart the gap clock while off track or in the pits.
                        _state[key] = (st.Last, f.Captured, st.Changes, st.MaxGap);
                    }
                }
            }
        }
    }

    public IReadOnlyList<FieldUpdate> Results()
    {
        lock (_gate)
        {
            return _state.Select(kv => new FieldUpdate(kv.Key.Item1, kv.Key.Item2, kv.Value.Changes,
                    kv.Value.Changes == 0 ? null : Math.Round(kv.Value.MaxGap.TotalSeconds, 3)))
                .OrderBy(r => r.Var, StringComparer.Ordinal).ThenBy(r => r.CarIdx).ToList();
        }
    }
}

/// <summary>
/// Spike 5: which per-car fields update live? Captures every frame (60 Hz) for a few minutes, counts how often each
/// field changes per car, and writes a 2 Hz recording of all cars that the web service uses as sorting test data.
/// The operator compares prompted snapshots with the sim's standings.
/// </summary>
internal static class Spike5Fields
{
    /// <summary>Per-car fields the sorting view needs (spec: Field sorting view, data needed from the agent).</summary>
    public static readonly string[] CarVars =
    [
        "CarIdxLapDistPct", "CarIdxLap", "CarIdxLapCompleted", "CarIdxOnPitRoad", "CarIdxTrackSurface",
        "CarIdxPosition", "CarIdxClassPosition",
    ];

    /// <summary>The continuous field that must change at least once per second while a car is on track.</summary>
    public const string ContinuousVar = "CarIdxLapDistPct";

    public const double MaxGapSeconds = 1.0;

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var minutes = env.Args.GetInt("minutes", 5, 1, 30);
        if (!await env.ConnectAsync(CarVars.Concat(["SessionFlags", "SessionState", "SessionNum"]), updateInterval: 1))
        {
            return 1;
        }

        using var log = env.CreateLog(5);
        var recordingDir = Path.Combine(Path.GetDirectoryName(env.LogDirectory)!, "recordings");
        Directory.CreateDirectory(recordingDir);
        var recordingPath = Path.Combine(recordingDir, $"spike5-{env.Clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z-{log.RunId}.jsonl");
        log.Write("header", env.Header(5, log.RunId, new { minutes, recording = recordingPath, vars = CarVars }));
        Console.WriteLine($"Log: {log.Path}");
        Console.WriteLine($"Recording: {recordingPath}");

        var session = env.Sim.Session!;
        var missing = CarVars.Where(v => !env.Sim.AvailableVars.Contains(v)).ToArray();
        if (missing.Length > 0)
        {
            Prompt.Warn("Not broadcast by the sim: " + string.Join(", ", missing));
        }

        var stats = new FieldUpdateStats(CarVars);
        void OnFrame(TelemetryFrame f) => stats.Observe(f, idx => env.Sim.Session?.CarByIdx(idx) is { IsPaceCar: false, IsSpectator: false });
        env.Sim.FrameCaptured += OnFrame;

        await using var recording = new StreamWriter(recordingPath, append: false, new System.Text.UTF8Encoding(false));
        await recording.WriteLineAsync(SpikeLogJson(new
        {
            type = "header",
            capturedAt = env.Clock.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            iracingBuild = session.BuildVersion,
            sessionNum = session.CurrentSessionNum,
            sessionType = session.CurrentSessionType,
            paceCarIdx = session.PaceCarIdx,
            cars = session.Cars.Where(c => !c.IsSpectator).Select(c => new { idx = c.CarIdx, num = c.CarNumber, classId = c.ClassId, isPaceCar = c.IsPaceCar }),
        }));

        Prompt.Heading($"Spike 5: recording for {minutes} min. Press Enter here to compare a snapshot with the sim's standings; type q and Enter to stop early.");
        var until = env.Clock.Elapsed + TimeSpan.FromMinutes(minutes);
        var nextFrame = env.Clock.Elapsed;
        var comparisons = 0;
        try
        {
            while (env.Clock.Elapsed < until)
            {
                if (env.Clock.Elapsed >= nextFrame)
                {
                    nextFrame += TimeSpan.FromMilliseconds(500);
                    await recording.WriteLineAsync(SpikeLogJson(Frame(env.Sim.Latest!, env.Sim.Session!)));
                }

                if (Prompt.KeyIfAvailable() is { } key)
                {
                    if (key.Key == ConsoleKey.Q)
                    {
                        break;
                    }

                    if (key.Key == ConsoleKey.Enter)
                    {
                        comparisons++;
                        var frame = env.Sim.Latest!;
                        PrintStandings(frame, env.Sim.Session!);
                        var matches = Prompt.YesNo("Does this match the sim's standings (positions, laps, pit road)?");
                        var note = Prompt.Ask("Note (which cars differ, blank for none):");
                        log.Write("comparison", new { n = comparisons, matches, note, frame = Frame(frame, env.Sim.Session!) });
                        Console.WriteLine("Recording continues. Enter = compare again, q + Enter = stop.");
                    }
                }

                await env.Clock.Delay(TimeSpan.FromMilliseconds(20), env.Cancellation);
            }
        }
        finally
        {
            env.Sim.FrameCaptured -= OnFrame;
        }

        var results = stats.Results();
        foreach (var r in results)
        {
            log.Write("fieldStats", new { var = r.Var, carIdx = r.CarIdx, car = env.Sim.Session!.CarByIdx(r.CarIdx)?.CarNumber, r.Changes, r.MaxGapSeconds });
        }

        var summary = Evaluate(results, missing, stats.Frames);
        log.Write("summary", summary);
        Console.WriteLine();
        Console.WriteLine($"Result: {summary.Result}. Frames {stats.Frames}. {string.Join(" ", summary.Reasons)}");
        Console.WriteLine($"Log: {log.Path}");
        return 0;
    }

    internal sealed record Spike5Summary(string Result, int Frames, IReadOnlyList<string> Missing, IReadOnlyDictionary<string, int> CarsWithChanges,
        double? WorstContinuousGapSeconds, IReadOnlyList<string> Reasons);

    /// <summary>
    /// Pass if every field is broadcast and lap distance changed at least once per second for every car while it was
    /// on track. Discrete fields (position, laps, pit road) change only when something happens, so they are reported,
    /// not judged.
    /// </summary>
    public static Spike5Summary Evaluate(IReadOnlyList<FieldUpdate> results, IReadOnlyList<string> missing, int frames)
    {
        var reasons = new List<string>();
        if (missing.Count > 0)
        {
            reasons.Add("missing: " + string.Join(", ", missing));
        }

        var continuous = results.Where(r => r.Var == ContinuousVar).ToList();
        var worst = continuous.Select(r => r.MaxGapSeconds).Where(g => g is not null).DefaultIfEmpty(null).Max();
        var stuck = continuous.Where(r => r.Changes == 0).Select(r => r.CarIdx).ToList();
        var slow = continuous.Where(r => r.MaxGapSeconds > MaxGapSeconds).Select(r => r.CarIdx).ToList();
        if (continuous.Count == 0)
        {
            reasons.Add("no lap distance data");
        }

        if (slow.Count > 0)
        {
            reasons.Add($"lap distance gap over {MaxGapSeconds} s for car index(es) {string.Join(", ", slow)}");
        }

        if (stuck.Count > 0)
        {
            reasons.Add($"lap distance never changed for car index(es) {string.Join(", ", stuck)} (parked or not moving?)");
        }

        var withChanges = results.GroupBy(r => r.Var).ToDictionary(g => g.Key, g => g.Count(r => r.Changes > 0));
        var result = missing.Count == 0 && continuous.Count > 0 && slow.Count == 0 ? "pass" : "fail";
        return new Spike5Summary(result, frames, missing, withChanges, worst, reasons);
    }

    /// <summary>One recording line: the snapshot fields per car, close to the snapshot contract.</summary>
    public static object Frame(TelemetryFrame f, SessionInfoView s)
    {
        int? I(string v, int idx) => f.Get(v, idx) is { } x ? Convert.ToInt32(x, CultureInfo.InvariantCulture) : null;
        var flags = f.Get("SessionFlags") is { } fl ? Convert.ToUInt32(fl, CultureInfo.InvariantCulture) : 0u;
        return new
        {
            type = "frame",
            sessionTime = Math.Round(f.SessionTime, 3),
            sessionNum = I("SessionNum", 0),
            sessionState = I("SessionState", 0),
            flags = SdkNames.SessionFlags(flags),
            cars = s.Cars.Where(c => !c.IsSpectator).Select(c => new
            {
                idx = c.CarIdx,
                num = c.CarNumber,
                classId = c.ClassId,
                isPaceCar = c.IsPaceCar,
                lap = I("CarIdxLap", c.CarIdx),
                lapCompleted = I("CarIdxLapCompleted", c.CarIdx),
                lapDistPct = f.Get("CarIdxLapDistPct", c.CarIdx) is { } d ? Math.Round(Convert.ToDouble(d, CultureInfo.InvariantCulture), 5) : (double?)null,
                onPitRoad = f.Get("CarIdxOnPitRoad", c.CarIdx) as bool?,
                surface = TelemetryDiffDescribe(I("CarIdxTrackSurface", c.CarIdx)),
                posOverall = I("CarIdxPosition", c.CarIdx),
                posClass = I("CarIdxClassPosition", c.CarIdx),
            }),
        };
    }

    private static string? TelemetryDiffDescribe(int? surface) => surface is { } v ? SdkNames.TrackSurface(v) : null;

    private static void PrintStandings(TelemetryFrame f, SessionInfoView s)
    {
        Console.WriteLine("  Pos  Cls  Car   Lap  Dist   Pit  Surface");
        var rows = s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar)
            .Select(c => (c, pos: f.Get("CarIdxPosition", c.CarIdx) is { } p ? Convert.ToInt32(p, CultureInfo.InvariantCulture) : 0))
            .OrderBy(r => r.pos <= 0 ? int.MaxValue : r.pos);
        foreach (var (c, pos) in rows)
        {
            var cls = f.Get("CarIdxClassPosition", c.CarIdx);
            var lap = f.Get("CarIdxLapCompleted", c.CarIdx);
            var dist = f.Get("CarIdxLapDistPct", c.CarIdx) is { } d ? Convert.ToDouble(d, CultureInfo.InvariantCulture) : -1;
            var pit = f.Get("CarIdxOnPitRoad", c.CarIdx) is true ? "pit" : "";
            var surface = f.Get("CarIdxTrackSurface", c.CarIdx) is { } sv ? SdkNames.TrackSurface(Convert.ToInt32(sv, CultureInfo.InvariantCulture)) : "?";
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {pos,3}  {cls,3}  #{c.CarNumber,-4} {lap,3}  {dist,5:F3}  {pit,-3}  {surface}"));
        }
    }

    private static string SpikeLogJson(object o) => System.Text.Json.JsonSerializer.Serialize(o, SpikeLog.JsonOptions);
}
