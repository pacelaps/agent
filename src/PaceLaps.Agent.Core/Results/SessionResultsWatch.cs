using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Core.Results;

/// <summary>
/// Turns iRacing's results into <c>session_results</c> events (spec-feedback 42): for each session whose results
/// changed since the last event, the data in the contract's terms (car numbers, class positions from 1, null for
/// iRacing's -1, no out reason while running). Placeholder rows before the first completed lap are not sent. No race
/// logic: it forwards what iRacing says. Pure apart from remembering what it sent.
/// </summary>
public sealed class SessionResultsWatch
{
    /// <summary>ResultsFastestLap's car index while there is no fastest lap.</summary>
    private const int NoCar = 255;

    private readonly Dictionary<int, string> _sent = [];

    /// <summary>The events due for this session info: one per session whose results changed.</summary>
    public IReadOnlyList<SessionResultsData> Observe(SimSession session)
    {
        var due = new List<SessionResultsData>();
        foreach (var results in session.Results)
        {
            if (Convert(session, results) is not { } data)
            {
                continue;
            }

            var key = ContractJson.Serialize(data);
            if (_sent.TryGetValue(data.SessionNum, out var previous) && previous == key)
            {
                continue;
            }

            _sent[data.SessionNum] = key;
            due.Add(data);
        }

        return due;
    }

    /// <summary>A session's results in the contract's terms; null before the first completed lap or without cars.</summary>
    public static SessionResultsData? Convert(SimSession session, SimSessionResults results)
    {
        if (results.LapsComplete < 1)
        {
            return null;
        }

        var cars = new List<ResultCarData>();
        foreach (var row in results.Rows.OrderBy(r => r.Position))
        {
            if (row.Position < 1 || session.CarByIdx(row.CarIdx) is not { IsPaceCar: false, IsSpectator: false } car)
            {
                continue;
            }

            cars.Add(new ResultCarData(
                car.Number,
                row.Position,
                Math.Max(0, row.ClassPosition) + 1,
                Math.Max(0, row.LapsComplete),
                Math.Max(0, row.Lap),
                Math.Round(Math.Max(0, row.Time), 4),
                Seconds(row.LastTime),
                Seconds(row.FastestTime),
                row.FastestLap >= 0 && row.FastestTime > 0 ? row.FastestLap : null,
                Math.Max(0, row.LapsLed),
                Math.Max(0, row.Incidents),
                row.ReasonOutId > 0 ? new ResultOutData(row.ReasonOutId, Trim(row.ReasonOutStr)) : null));
        }

        if (cars.Count == 0)
        {
            return null;
        }

        var fastest = results.FastestLap is { } f && f.CarIdx != NoCar && f.Time > 0 && session.CarByIdx(f.CarIdx) is { } fastestCar
            ? new FastestLapData(fastestCar.Number, Math.Max(0, f.Lap), Math.Round(f.Time, 4))
            : null;
        return new SessionResultsData(results.SessionNum, results.Official, results.LapsComplete, fastest, cars);
    }

    private static double? Seconds(double value) => value > 0 ? Math.Round(value, 4) : null;

    private static string Trim(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= 100 ? t : t[..100];
    }
}
