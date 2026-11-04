using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes.Guided;

/// <summary>
/// Records telemetry changes for the whole guided run, also between steps, so effects that come late
/// (pace mode, caution length) are in the log. Each record names the last step sent and the seconds since.
/// While a step is being watched, it also feeds that step's expectation trackers and prints changes.
/// </summary>
internal sealed class GuidedRecorder
{
    private readonly SimConnection _sim;
    private readonly SpikeLog _log;
    private readonly IClock _clock;
    private readonly Func<string?> _car;
    private readonly Func<string?> _car2;
    private readonly object _gate = new();
    private string _step = "(before first step)";
    private TimeSpan _stepSentAt;
    private IReadOnlyList<ExpectationTracker> _trackers = [];
    private bool _watching;

    public GuidedRecorder(SimConnection sim, SpikeLog log, IClock clock, Func<string?> car, Func<string?> car2)
    {
        _sim = sim;
        _log = log;
        _clock = clock;
        _car = car;
        _car2 = car2;
    }

    /// <summary>A step was sent: later records refer to it. While watching, trackers are fed and changes printed.</summary>
    public void BeginWatch(string step, TimeSpan sentAt, IReadOnlyList<ExpectationTracker> trackers)
    {
        lock (_gate)
        {
            _step = step;
            _stepSentAt = sentAt;
            _trackers = trackers;
            _watching = true;
        }
    }

    public void EndWatch()
    {
        lock (_gate)
        {
            _watching = false;
            _trackers = [];
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var previous = _sim.Latest!;
        var sessionUpdate = _sim.Session!.UpdateCount;
        var lastSample = TimeSpan.Zero;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.Delay(TimeSpan.FromMilliseconds(100), ct);
                var frame = _sim.Latest!;
                if (frame.TickCount == previous.TickCount)
                {
                    continue;
                }

                var session = _sim.Session!;
                lock (_gate)
                {
                    var since = _clock.Elapsed - _stepSentAt;
                    var s = Math.Round(since.TotalSeconds, 2);
                    foreach (var c in TelemetryDiff.Diff(previous, frame, TelemetryDiff.DiscreteVars, idx => session.CarByIdx(idx) is not null))
                    {
                        var num = c.Var.StartsWith("CarIdx", StringComparison.Ordinal) ? session.CarByIdx(c.Index)?.CarNumber : null;
                        _log.Write("change", new
                        {
                            step = _step,
                            afterSeconds = s,
                            sessionTime = Math.Round(frame.SessionTime, 2),
                            var = c.Var,
                            carIdx = num is null ? (int?)null : c.Index,
                            car = num,
                            old = c.Old,
                            @new = c.New,
                            oldText = TelemetryDiff.Describe(c.Var, c.Old),
                            newText = TelemetryDiff.Describe(c.Var, c.New),
                        });
                        if (_watching && IsInteresting(c, num))
                        {
                            Console.WriteLine($"  +{s,6:F1}s {c.Var}{(num is null ? "" : $" #{num}")}: {TelemetryDiff.Describe(c.Var, c.Old)} -> {TelemetryDiff.Describe(c.Var, c.New)}");
                        }
                    }

                    foreach (var t in _trackers)
                    {
                        if (t.Observe(frame, since) && _watching)
                        {
                            Console.WriteLine($"  +{s,6:F1}s telemetry shows {t.Kind}");
                        }
                    }

                    if (_clock.Elapsed - lastSample >= TimeSpan.FromSeconds(1))
                    {
                        lastSample = _clock.Elapsed;
                        _log.Write("pace", PaceSample(frame, session, _step, s));
                    }

                    if (session.UpdateCount != sessionUpdate)
                    {
                        sessionUpdate = session.UpdateCount;
                        _log.Write("sessionInfo", new { step = _step, afterSeconds = s, updateCount = sessionUpdate, sessionNum = session.CurrentSessionNum, sessionType = session.CurrentSessionType });
                    }
                }

                previous = frame;
            }
        }
        catch (OperationCanceledException)
        {
            // End of the run.
        }
    }

    /// <summary>Changes to print live: everything session-wide, per-car flags, and anything about the target cars.</summary>
    private bool IsInteresting(TelemetryChange c, string? num) =>
        num is null || num == _car() || num == _car2() || c.Var is "CarIdxSessionFlags" or "CarIdxPaceFlags";

    public static object PaceSample(TelemetryFrame f, SessionInfoView session, string step, double s)
    {
        var p = session.PaceCarIdx;
        var gap = Expectations.PaceCarGap(f, p);
        return new
        {
            step,
            afterSeconds = s,
            sessionTime = Math.Round(f.SessionTime, 2),
            paceCar = p < 0 ? null : new
            {
                surface = TelemetryDiff.Describe("CarIdxTrackSurface", f.Get("CarIdxTrackSurface", p)),
                lapDistPct = f.Get("CarIdxLapDistPct", p),
                onPitRoad = f.Get("CarIdxOnPitRoad", p),
            },
            leader = gap is { } g ? session.CarByIdx(g.LeaderIdx)?.CarNumber : null,
            leaderLap = gap is { } g1 ? f.Get("CarIdxLapCompleted", g1.LeaderIdx) : null,
            gapLapFraction = gap is { } g2 ? Math.Round(g2.Gap, 4) : (double?)null,
        };
    }
}
