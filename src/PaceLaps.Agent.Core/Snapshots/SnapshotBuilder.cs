using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Core.Snapshots;

/// <summary>
/// Builds the snapshot message (1 to 2 Hz) from the session info and the latest telemetry frame. Pure function.
/// Field mappings are provisional until spike 5 confirms which fields update live:
/// lap = laps completed; connected = the car is in the world (not NotInWorld).
/// </summary>
public static class SnapshotBuilder
{
    public static SnapshotMessage Build(string agentId, long seq, DateTimeOffset capturedAt, SimSession session, SimFrame frame) => new()
    {
        AgentId = agentId,
        Seq = seq,
        CapturedAt = capturedAt,
        Session = new SnapshotSession
        {
            LeagueId = Math.Max(0, session.LeagueId),
            SeasonId = Math.Max(0, session.SeasonId),
            SessionId = Math.Max(0, session.SessionId),
            SubSessionId = Math.Max(0, session.SubSessionId),
            Num = Math.Max(0, frame.SessionNum),
            Type = session.SessionType,
            State = SimValues.SessionStateName(frame.SessionState),
            TimeSec = Math.Round(Math.Max(0, frame.SessionTime), 3),
            Flags = FlagNormalizer.Normalize(frame.SessionFlags, frame.SessionState),
            Track = session.Track is { } t
                ? new SnapshotTrack
                {
                    Id = t.Id,
                    Name = Cut(t.Name),
                    Config = Cut(t.Config),
                    LengthM = t.LengthM,
                    SectorStarts = SectorStarts(t.SectorStarts),
                }
                : null,
            Laps = Laps(frame.Length),
            Time = Time(frame.Length),
            Weather = Weather(frame.Weather),
            Classes = Classes(session.Cars),
        },
        Cars = session.Cars.Where(c => !c.IsSpectator).Select(c => Car(c, frame.Car(c.CarIdx))).ToList(),
    };

    private static SnapshotCar Car(SimCar car, SimCarState? state)
    {
        var s = state ?? new SimCarState(0, 0, -1, false, SimValues.SurfaceNotInWorld, 0, 0, 0, 0);
        return new SnapshotCar
        {
            Num = car.Number,
            CustId = car.CustId > 0 ? car.CustId : null,
            ClassId = car.ClassId,
            Lap = Math.Max(0, s.LapCompleted),
            LapDistPct = Math.Round(Math.Clamp(s.LapDistPct, 0f, 1f), 5),
            OnPitRoad = s.OnPitRoad,
            Surface = SimValues.SurfaceName(s.Surface),
            PosClass = Math.Max(0, s.ClassPosition),
            PosOverall = Math.Max(0, s.Position),
            IsPaceCar = car.IsPaceCar,
            Incidents = car.Incidents is >= 0 ? car.Incidents : null,
            Connected = s.Surface != SimValues.SurfaceNotInWorld,
            LastLapTime = s.LastLapTime > 0 ? Math.Round(s.LastLapTime, 4) : null,
            BestLapTime = s.BestLapTime > 0 ? Math.Round(s.BestLapTime, 4) : null,
        };
    }

    /// <summary>
    /// One class per class id the cars carry (spectators and the pace car left out), with iRacing's values as the
    /// contract names them: the name trimmed and left out when empty, the colour 0xrrggbb as #rrggbb, the models.
    /// </summary>
    private static List<SnapshotClass>? Classes(IReadOnlyList<SimCar> cars)
    {
        var classes = cars.Where(c => !c.IsSpectator && !c.IsPaceCar).GroupBy(c => c.ClassId).OrderBy(g => g.Key).Select(g =>
        {
            var models = g.Select(c => c.Model?.Trim()).Where(m => !string.IsNullOrEmpty(m)).Select(m => Cut(m!)).Distinct().Take(64).ToList();
            return new SnapshotClass
            {
                Id = Math.Max(0, g.Key),
                Name = g.Select(c => c.ClassName?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) is { } name ? Cut(name, 60) : null,
                Colour = g.Select(c => Colour(c.ClassColour)).FirstOrDefault(c => c is not null),
                Models = models.Count > 0 ? models : null,
            };
        }).Take(64).ToList();

        return classes.Count > 0 ? classes : null;
    }

    /// <summary>iRacing's 0xrrggbb (or a bare number) as #rrggbb; null for none or anything else.</summary>
    internal static string? Colour(string? value)
    {
        var hex = value?.Trim();
        if (hex is null)
        {
            return null;
        }

        hex = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return hex.Length is > 0 and <= 6 && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb) && rgb > 0
            ? $"#{rgb:x6}"
            : null;
    }

    /// <summary>Laps of a lap-limited session; none for iRacing's unlimited (32767).</summary>
    private static SnapshotLaps? Laps(SimSessionLength? l) =>
        l is { LapsTotal: > 0 and < UnlimitedLaps } ? new SnapshotLaps(l.LapsTotal, Math.Clamp(l.LapsRemainEx, 0, l.LapsTotal)) : null;

    /// <summary>Time of a timed session: shorter than a day (a lap race showed 86400, after the finish 604799).</summary>
    private static SnapshotTime? Time(SimSessionLength? l) =>
        l is { TimeTotal: > 0 and < OneDaySec } ? new SnapshotTime(Math.Round(l.TimeTotal, 1), Math.Round(Math.Max(0, l.TimeRemain), 1)) : null;

    private const int UnlimitedLaps = 32767;
    private const double OneDaySec = 86400;

    private static readonly string[] WetnessNames = ["dry", "mostly_dry", "very_lightly_wet", "lightly_wet", "moderately_wet", "very_wet", "extremely_wet"];
    private static readonly string[] SkiesNames = ["clear", "partly_cloudy", "mostly_cloudy", "overcast"];

    /// <summary>The weather as the contract names it: wetness 0 (unknown) and unknown sky values left out.</summary>
    private static SnapshotWeather? Weather(SimWeather? w)
    {
        if (w is null)
        {
            return null;
        }

        var direction = Math.Round((w.WindDir * 180 / Math.PI % 360 + 360) % 360, 1);
        return new SnapshotWeather
        {
            AirTempC = Math.Round(w.AirTemp, 1),
            TrackTempC = Math.Round(w.TrackTemp, 1),
            Wetness = w.Wetness is >= 1 and <= 7 ? WetnessNames[w.Wetness - 1] : null,
            PrecipitationPct = Percent(w.Precipitation),
            DeclaredWet = w.DeclaredWet,
            Skies = w.Skies is >= 0 and <= 3 ? SkiesNames[w.Skies] : null,
            WindSpeedMs = Math.Round(Math.Max(0, w.WindVel), 2),
            WindDirDeg = direction >= 360 ? 0 : direction,
            HumidityPct = Percent(w.Humidity),
            FogPct = Percent(w.Fog),
        };
    }

    private static double Percent(float share) => Math.Round(Math.Clamp(share, 0f, 1f) * 100, 1);

    /// <summary>The sector starts as the contract takes them (0 to below 1, at most 50); none without.</summary>
    private static IReadOnlyList<double>? SectorStarts(IReadOnlyList<double> starts) =>
        starts.Count is > 0 and <= 50 && starts.All(x => x is >= 0 and < 1) ? starts.Select(x => Math.Round(x, 5)).ToList() : null;

    /// <summary>Track and layout names fit the contract (at most 100 characters).</summary>
    private static string Cut(string text, int max = 100) => text.Length <= max ? text : text[..max];
}
