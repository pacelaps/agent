using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>An incident count seen in one source at a monotonic time.</summary>
internal sealed record IncidentObservation(string Source, int CarIdx, int Old, int New, TimeSpan At);

/// <summary>How long after the driver's own telemetry a session-info source showed the new count.</summary>
internal sealed record IncidentLatency(int Old, int New, TimeSpan TelemetryAt, IReadOnlyDictionary<string, double?> LatencySecondsBySource);

/// <summary>
/// Spike 4: how quickly do incident counts update for all drivers? The operator drives and causes a 1x and a 4x.
/// The operator's own telemetry count (<c>PlayerCarMyIncidentCount</c>) changes at once; the session info is what
/// every other driver and a spectator see. The latency is the time from the telemetry change to the session info
/// showing the same total. AI incidents are logged too, with the last off-track moment as a rough reference.
/// </summary>
internal static class Spike4Incidents
{
    public const double PassSeconds = 2.0;

    /// <summary>The session-info sources compared against telemetry.</summary>
    public static readonly string[] SessionSources = ["resultsPositions", "driverInfoCar", "driverIncidentCount"];

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var maxMinutes = env.Args.GetInt("minutes", 15, 1, 120);
        if (!await env.ConnectAsync(["PlayerCarMyIncidentCount", "PlayerCarDriverIncidentCount", "PlayerCarTeamIncidentCount",
                "CarIdxTrackSurface", "CarIdxOnPitRoad"], updateInterval: 1))
        {
            return 1;
        }

        var start = env.Sim.Session!;
        if (start.DriverCarIdx < 0 || start.CarByIdx(start.DriverCarIdx) is not { IsSpectator: false })
        {
            Prompt.Warn("You are not in a car. Spike 4 needs you driving: join as a driver, then run again.");
            return 1;
        }

        using var log = env.CreateLog(4);
        log.Write("header", env.Header(4, log.RunId, new { maxMinutes }));
        Console.WriteLine($"Log: {log.Path}");

        var gate = new object();
        var observations = new List<IncidentObservation>();
        int? ownTelemetry = null;
        var lastOffTrack = new Dictionary<int, TimeSpan>();
        int[]? surfaces = null;

        // 60 Hz on the SDK thread: only compare numbers and record, nothing slow.
        void OnFrame(TelemetryFrame f)
        {
            lock (gate)
            {
                if (f.Get("PlayerCarMyIncidentCount") is int own)
                {
                    if (ownTelemetry is { } old && old != own)
                    {
                        observations.Add(new IncidentObservation("telemetry", start.DriverCarIdx, old, own, f.Captured));
                    }

                    ownTelemetry = own;
                }

                if (f.Values.TryGetValue("CarIdxTrackSurface", out var s))
                {
                    var now = s.Select(v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    if (surfaces is not null)
                    {
                        for (var i = 0; i < now.Length && i < surfaces.Length; i++)
                        {
                            if (now[i] == 0 && surfaces[i] != 0)
                            {
                                lastOffTrack[i] = f.Captured;
                            }
                        }
                    }

                    surfaces = now;
                }
            }
        }

        env.Sim.FrameCaptured += OnFrame;
        Prompt.Heading($"Spike 4: drive and cause incidents (up to {maxMinutes} min). Press Enter here when done.");
        Console.WriteLine("Suggested: one off-track (1x), later a contact with an AI car (4x, or 2x/0x depending on the contact). Note what you did.");

        var previousInfo = SessionCounts(start);
        var updateCount = start.UpdateCount;
        var until = env.Clock.Elapsed + TimeSpan.FromMinutes(maxMinutes);
        var printed = 0;
        try
        {
            while (env.Clock.Elapsed < until && !Prompt.EnterPressed())
            {
                await env.Clock.Delay(TimeSpan.FromMilliseconds(50), env.Cancellation);
                var info = env.Sim.Session!;
                if (info.UpdateCount != updateCount)
                {
                    updateCount = info.UpdateCount;
                    var counts = SessionCounts(info);
                    foreach (var ((source, idx), value) in counts)
                    {
                        if (previousInfo.TryGetValue((source, idx), out var old) && old != value)
                        {
                            lock (gate)
                            {
                                observations.Add(new IncidentObservation(source, idx, old, value, info.ReceivedElapsed));
                            }
                        }
                    }

                    previousInfo = counts;
                }

                lock (gate)
                {
                    for (; printed < observations.Count; printed++)
                    {
                        var o = observations[printed];
                        var num = info.CarByIdx(o.CarIdx)?.CarNumber ?? "?";
                        var offTrack = lastOffTrack.TryGetValue(o.CarIdx, out var ot) ? Math.Round((o.At - ot).TotalSeconds, 2) : (double?)null;
                        log.Write("incident", new { source = o.Source, carIdx = o.CarIdx, car = num, own = o.CarIdx == start.DriverCarIdx, old = o.Old, @new = o.New, atSeconds = Math.Round(o.At.TotalSeconds, 3), secondsSinceOffTrack = offTrack });
                        if (o.CarIdx == start.DriverCarIdx)
                        {
                            Console.WriteLine($"  {o.Source,-20} #{num} {o.Old} -> {o.New}");
                        }
                    }
                }
            }
        }
        finally
        {
            env.Sim.FrameCaptured -= OnFrame;
        }

        List<IncidentObservation> all;
        lock (gate)
        {
            all = [.. observations];
        }

        var latencies = Match(all, start.DriverCarIdx);
        foreach (var l in latencies)
        {
            log.Write("latency", new { l.Old, l.New, telemetryAtSeconds = Math.Round(l.TelemetryAt.TotalSeconds, 3), latencySeconds = l.LatencySecondsBySource });
        }

        Prompt.Heading("Done. Answer:");
        var what = Prompt.Ask("What did you do (for example: off track at T3, then contact with #12)?");
        var spectatorNote = Prompt.Ask("Anything you noticed about when the count changed in the sim's own display (blank for none)?");
        var result = Evaluate(latencies);
        log.Write("summary", new { result, incidents = latencies.Count, what, spectatorNote, passSeconds = PassSeconds });

        Console.WriteLine();
        foreach (var l in latencies)
        {
            Console.WriteLine($"  {l.Old} -> {l.New}: " + string.Join(", ", l.LatencySecondsBySource.Select(kv => $"{kv.Key} {(kv.Value is { } v ? $"{v:F2} s" : "not seen")}")));
        }

        Console.WriteLine($"Result: {result}. Log: {log.Path}");
        return 0;
    }

    /// <summary>Every session-info incident count, keyed by (source, car index).</summary>
    public static Dictionary<(string Source, int CarIdx), int> SessionCounts(SessionInfoView s)
    {
        var counts = new Dictionary<(string, int), int>();
        foreach (var (idx, n) in s.ResultIncidents)
        {
            counts[("resultsPositions", idx)] = n;
        }

        foreach (var c in s.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar))
        {
            counts[("driverInfoCar", c.CarIdx)] = c.CurDriverIncidentCount;
        }

        if (s.DriverCarIdx >= 0)
        {
            counts[("driverIncidentCount", s.DriverCarIdx)] = s.DriverIncidentCount;
        }

        return counts;
    }

    /// <summary>
    /// For each change of the operator's telemetry count, the time until each session-info source first showed a
    /// total at least as high. Null when a source never got there.
    /// </summary>
    public static List<IncidentLatency> Match(IReadOnlyList<IncidentObservation> observations, int ownCarIdx)
    {
        var result = new List<IncidentLatency>();
        foreach (var t in observations.Where(o => o.Source == "telemetry").OrderBy(o => o.At))
        {
            var bySource = new Dictionary<string, double?>();
            foreach (var source in SessionSources)
            {
                var first = observations
                    .Where(o => o.Source == source && o.CarIdx == ownCarIdx && o.New >= t.New && o.At >= t.At)
                    .OrderBy(o => o.At)
                    .FirstOrDefault();
                bySource[source] = first is null ? null : Math.Round((first.At - t.At).TotalSeconds, 3);
            }

            result.Add(new IncidentLatency(t.Old, t.New, t.At, bySource));
        }

        return result;
    }

    /// <summary>
    /// Pass if every incident reached the driver list (what other drivers see) within 2 s. Spike 4 showed the
    /// results positions are not a live source: they did not change during the run.
    /// </summary>
    public static string Evaluate(IReadOnlyList<IncidentLatency> latencies)
    {
        if (latencies.Count == 0)
        {
            return "invalid (no incident recorded)";
        }

        var others = latencies.Select(l => l.LatencySecondsBySource["driverInfoCar"]).ToList();
        return others.All(v => v is { } s && s <= PassSeconds) ? "pass" : "fail";
    }
}
