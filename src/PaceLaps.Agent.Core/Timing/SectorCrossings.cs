using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Core.Timing;

/// <summary>
/// Notices when cars cross the start of a sector, for race control's lap times (spec-feedback 39). Fed every telemetry frame; between two frames of a car it finds the sector
/// starts its lap distance passed and interpolates when, to a few milliseconds at 60 Hz. Only measures: sector times,
/// lap times and bests are the server's. The lap times check (spike-results.md, 2026-11-02) found the sum of the
/// measured sectors within 0.001 s of iRacing's lap time.
/// </summary>
/// <remarks>
/// A car that moves more than <see cref="JumpLap"/> of a lap between two frames, or backwards, or is missing for more
/// than <see cref="Gap"/>, jumped (tow, reset, a gap in the data): no crossing for it, and its next crossing says so.
/// Pit road and off track are remembered from one crossing to the next. A new session number starts over.
/// </remarks>
public sealed class SectorCrossings
{
    public const double JumpLap = 0.2;
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(0.5);

    private readonly Dictionary<string, Car> _cars = new(StringComparer.Ordinal);
    private int _sessionNum = -1;

    private sealed class Car
    {
        public double Pct;
        public double Time;
        public bool Pit;
        public bool Off;
        public bool Jump;
    }

    /// <summary>The crossings since the previous frame, in time order.</summary>
    public IReadOnlyList<SectorCrossingData> Observe(SimSession session, SimFrame frame)
    {
        var starts = session.Track?.SectorStarts ?? [];
        if (starts.Count == 0)
        {
            return [];
        }

        if (frame.SessionNum != _sessionNum)
        {
            _cars.Clear();
            _sessionNum = frame.SessionNum;
        }

        var found = new List<SectorCrossingData>();
        foreach (var car in session.Cars)
        {
            if (car.IsPaceCar || car.IsSpectator || frame.Car(car.CarIdx) is not { } state)
            {
                continue;
            }

            if (state.LapDistPct < 0 || state.Surface == SimValues.SurfaceNotInWorld)
            {
                if (_cars.TryGetValue(car.Number, out var away))
                {
                    away.Jump = true;
                    away.Time = double.NaN; // no reading to interpolate from until it is back
                }

                continue;
            }

            var pct = (double)state.LapDistPct;
            if (!_cars.TryGetValue(car.Number, out var c))
            {
                _cars[car.Number] = new Car { Pct = pct, Time = frame.SessionTime, Pit = state.OnPitRoad, Off = Off(state) };
                continue;
            }

            var moved = (pct - c.Pct + 1) % 1;
            var dt = frame.SessionTime - c.Time;
            if (double.IsNaN(dt) || dt <= 0 && moved > 0 || dt > Gap.TotalSeconds || moved > JumpLap)
            {
                c.Jump = true;
            }
            else if (moved > 0 && dt > 0)
            {
                foreach (var (sector, at) in Crossed(starts, c.Pct, moved, c.Time, dt))
                {
                    found.Add(new SectorCrossingData(car.Number, sector, Math.Max(0, state.LapCompleted), Math.Round(at, 4),
                        c.Pit ? true : null, c.Off ? true : null, c.Jump ? true : null));
                    c.Pit = c.Off = c.Jump = false;
                }
            }

            c.Pct = pct;
            c.Time = frame.SessionTime;
            c.Pit |= state.OnPitRoad;
            c.Off |= Off(state);
        }

        found.Sort((a, b) => a.SessionTimeSec.CompareTo(b.SessionTimeSec));
        return found;
    }

    /// <summary>The sector starts between <paramref name="from"/> and <paramref name="moved"/> of a lap further, with when.</summary>
    private static IEnumerable<(int Sector, double At)> Crossed(IReadOnlyList<double> starts, double from, double moved, double t0, double dt) =>
        starts.Select((s, i) => (Sector: i, Ahead: (s - from + 1) % 1))
            .Where(x => x.Ahead > 0 && x.Ahead <= moved)
            .OrderBy(x => x.Ahead)
            .Select(x => (x.Sector, t0 + dt * x.Ahead / moved));

    private static bool Off(SimCarState state) => state.Surface == SimValues.SurfaceOffTrack;
}
