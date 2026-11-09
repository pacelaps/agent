using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Snapshots;

namespace PaceLaps.Agent.Core.Events;

/// <summary>An event before it gets a sequence number.</summary>
public sealed record DetectedEvent(EventType Type, object Data);

/// <summary>
/// Compares successive views of the sim and reports flag changes, session changes and incident count increases.
/// The first observation only sets the baseline. Incident counts that go down (a new session resets them) reset
/// the baseline without an event. So does a new driver in a car (a team race's driver swap): iRacing's
/// CurDriverIncidentCount is the count of the driver at the wheel, so the incoming driver's points from an earlier
/// stint would otherwise read as a new incident.
/// </summary>
public sealed class EventDetector
{
    private IReadOnlyList<string>? _flags;
    private SessionChangeData? _identity;
    private Dictionary<string, (int Total, int CustId)>? _incidents;

    public IReadOnlyList<DetectedEvent> Observe(SimSession session, SimFrame frame)
    {
        var events = new List<DetectedEvent>();

        var identity = new SessionChangeData(session.LeagueId, session.SeasonId, session.SessionId, session.SubSessionId,
            Math.Max(0, frame.SessionNum), session.SessionType);
        if (_identity is not null && _identity != identity)
        {
            events.Add(new DetectedEvent(EventType.SessionChange, identity));
        }

        _identity = identity;

        var flags = FlagNormalizer.Normalize(frame.SessionFlags, frame.SessionState);
        if (_flags is not null && !_flags.SequenceEqual(flags))
        {
            events.Add(new DetectedEvent(EventType.FlagChange, new FlagChangeData(flags, _flags, identity.Num, Math.Round(frame.SessionTime, 3))));
        }

        _flags = flags;

        var incidents = session.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar && c.Incidents is not null)
            .GroupBy(c => c.Number)
            .ToDictionary(g => g.Key, g => (Total: g.First().Incidents!.Value, g.First().CustId));
        if (_incidents is not null)
        {
            foreach (var (car, (total, custId)) in incidents)
            {
                if (_incidents.TryGetValue(car, out var seen) && seen.CustId == custId && total > seen.Total)
                {
                    var c = session.CarByNumber(car);
                    var state = c is null ? null : frame.Car(c.CarIdx);
                    events.Add(new DetectedEvent(EventType.Incident, new IncidentData(car, total - seen.Total, total, identity.Num,
                        Math.Round(frame.SessionTime, 3), Math.Max(0, state?.LapCompleted ?? 0), Math.Round(Math.Clamp(state?.LapDistPct ?? 0f, 0f, 1f), 4))));
                }
            }
        }

        _incidents = incidents;
        return events;
    }
}
