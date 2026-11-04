namespace PaceLaps.Agent.Core.Calibration;

/// <summary>
/// Counts each car's clean laps for race control's lap start calibration. A clean lap runs from one lap-count change to the next without a moment on pit road; the partial lap
/// when recording starts and the out-lap do not count. A lap is named by the laps completed while it is driven.
/// Recording is done when every car out on track has the wanted number of clean laps. A car on pit road or out of the
/// world (towed after a crash) is not waited for; once it is back on track it counts again from its next full lap (its
/// clean laps stay).
/// </summary>
public sealed class CleanLaps
{
    private readonly Dictionary<string, Car> _cars = new(StringComparer.Ordinal);

    private sealed class Car
    {
        public int Lap;
        public bool Clean;
        public bool Out;
        public readonly List<int> Done = [];
    }

    /// <summary>The clean laps per car number (laps completed while each was driven).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> Done =>
        _cars.Where(c => c.Value.Done.Count > 0).ToDictionary(c => c.Key, c => (IReadOnlyList<int>)c.Value.Done.ToList(), StringComparer.Ordinal);

    /// <summary>Feeds one reading; returns the lap (laps completed while it was driven) that was just completed
    /// cleanly, if one was.</summary>
    public int? Observe(string car, int lapsCompleted, bool onPitRoad, bool inWorld)
    {
        if (!_cars.TryGetValue(car, out var c))
        {
            if (!inWorld || onPitRoad || lapsCompleted < 0)
            {
                return null;
            }

            _cars[car] = new Car { Lap = lapsCompleted, Clean = false };
            return null;
        }

        if (!inWorld || onPitRoad)
        {
            c.Out = true;
            return null;
        }

        if (c.Out)
        {
            // Back on track: the lap it is in does not count (it began on pit road or with a tow).
            c.Out = false;
            c.Lap = lapsCompleted;
            c.Clean = false;
            return null;
        }

        int? done = null;
        if (lapsCompleted > c.Lap)
        {
            if (c.Clean && lapsCompleted == c.Lap + 1)
            {
                c.Done.Add(c.Lap);
                done = c.Lap;
            }

            c.Lap = lapsCompleted;
            c.Clean = true;
        }

        return done;
    }

    /// <summary>Every car still out on track has at least <paramref name="laps"/> clean laps (and there is one).</summary>
    public bool Finished(int laps)
    {
        var running = _cars.Values.Where(c => !c.Out).ToList();
        return running.Count > 0 && running.All(c => c.Done.Count >= laps);
    }

    /// <summary>Clean laps so far per car still out on track, for the progress line.</summary>
    public IReadOnlyDictionary<string, int> Progress =>
        _cars.Where(c => !c.Value.Out).ToDictionary(c => c.Key, c => c.Value.Done.Count, StringComparer.Ordinal);
}
