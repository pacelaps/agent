using System.Globalization;
using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.IRacing.Sdk;

/// <summary>
/// Presents a <see cref="SimConnection"/> as the iRacing-independent <see cref="ISimData"/>. Start the connection
/// with <see cref="TelemetryVars"/>. Incidents come from the driver list's CurDriverIncidentCount: spike 4 showed
/// it updates for every car within about 0.6 s, while the results positions did not update during a race.
/// </summary>
public sealed class SimDataAdapter : ISimData, ISimFrames
{
    public const int MaxCars = 64;

    public static readonly string[] TelemetryVars =
    [
        "SessionTime", "SessionNum", "SessionState", "SessionFlags", "PaceMode",
        "CarIdxLap", "CarIdxLapCompleted", "CarIdxLapDistPct", "CarIdxOnPitRoad", "CarIdxTrackSurface",
        "CarIdxPosition", "CarIdxClassPosition", "CarIdxSessionFlags", "CarIdxPaceFlags",
        "CarIdxLastLapTime", "CarIdxBestLapTime",
        "SessionLapsTotal", "SessionLapsRemainEx", "SessionTimeTotal", "SessionTimeRemain",
        "AirTemp", "TrackTempCrew", "TrackWetness", "Precipitation", "WeatherDeclaredWet", "Skies", "WindVel", "WindDir",
        "RelativeHumidity", "FogLevel",
    ];

    private readonly SimConnection _sim;
    private SessionInfoView? _sessionSource;
    private SimSession? _session;
    private TelemetryFrame? _frameSource;
    private SimFrame? _frame;

    public SimDataAdapter(SimConnection sim)
    {
        _sim = sim;
        _sim.FrameCaptured += f =>
        {
            if (FrameArrived is { } handler)
            {
                handler(Map(f));
            }
        };
    }

    /// <inheritdoc />
    public event Action<SimFrame>? FrameArrived;

    public bool IsConnected => _sim.IsConnected;

    public SimSession? Session
    {
        get
        {
            var source = _sim.Session;
            if (!ReferenceEquals(source, _sessionSource))
            {
                _sessionSource = source;
                _session = source is null ? null : Map(source);
            }

            return _session;
        }
    }

    public SimFrame? Frame
    {
        get
        {
            var source = _sim.Latest;
            if (!ReferenceEquals(source, _frameSource))
            {
                _frameSource = source;
                _frame = source is null ? null : Map(source);
            }

            return _frame;
        }
    }

    public static SimSession Map(SessionInfoView s) => new()
    {
        UpdateCount = s.UpdateCount,
        BuildVersion = s.BuildVersion,
        LeagueId = s.LeagueId,
        SeasonId = s.SeasonId,
        SessionId = s.SessionId,
        SubSessionId = s.SubSessionId,
        SessionNum = s.CurrentSessionNum,
        SessionType = s.CurrentSessionType ?? "Unknown",
        DriverCarIdx = s.DriverCarIdx,
        DriverIsAdmin = s.DriverIsAdmin,
        OperatorCustId = s.OperatorCustId,
        OperatorName = s.OperatorName,
        PaceCarIdx = s.PaceCarIdx,
        Track = SimTrack.From(s.TrackId, s.TrackDisplayName, s.TrackConfigName, s.TrackLength) is { } track
            ? track with { SectorStarts = s.SectorStarts }
            : null,
        Cars = s.Cars.Select(c => new SimCar(c.CarIdx, c.CarNumber, c.ClassId, c.IsPaceCar, c.IsSpectator, c.UserId,
            c.IsPaceCar ? null : c.CurDriverIncidentCount, c.ClassShortName, c.ClassColor, c.CarScreenName)).ToList(),
        Results = s.Results,
    };

    public static SimFrame Map(TelemetryFrame f)
    {
        var cars = new SimCarState[MaxCars];
        for (var i = 0; i < MaxCars; i++)
        {
            cars[i] = new SimCarState(
                Int(f, "CarIdxLap", i, 0),
                Int(f, "CarIdxLapCompleted", i, 0),
                f.Get("CarIdxLapDistPct", i) is { } d ? Convert.ToSingle(d, CultureInfo.InvariantCulture) : -1f,
                f.Get("CarIdxOnPitRoad", i) is true,
                Int(f, "CarIdxTrackSurface", i, -1),
                Int(f, "CarIdxPosition", i, 0),
                Int(f, "CarIdxClassPosition", i, 0),
                UInt(f, "CarIdxSessionFlags", i),
                UInt(f, "CarIdxPaceFlags", i),
                Float(f, "CarIdxLastLapTime", i),
                Float(f, "CarIdxBestLapTime", i));
        }

        return new SimFrame
        {
            Captured = f.Captured,
            Tick = f.TickCount,
            SessionTime = f.SessionTime,
            SessionNum = Int(f, "SessionNum", 0, 0),
            SessionState = Int(f, "SessionState", 0, 0),
            SessionFlags = UInt(f, "SessionFlags", 0),
            PaceMode = Int(f, "PaceMode", 0, -1),
            Cars = cars,
            Length = f.Get("SessionLapsTotal", 0) is null ? null : new SimSessionLength(Int(f, "SessionLapsTotal", 0, 0),
                Int(f, "SessionLapsRemainEx", 0, 0), Double(f, "SessionTimeTotal"), Double(f, "SessionTimeRemain")),
            Weather = f.Get("AirTemp", 0) is null ? null : new SimWeather(Float(f, "AirTemp", 0), Float(f, "TrackTempCrew", 0),
                Int(f, "TrackWetness", 0, 0), Float(f, "Precipitation", 0), f.Get("WeatherDeclaredWet", 0) is true, Int(f, "Skies", 0, -1),
                Float(f, "WindVel", 0), Float(f, "WindDir", 0), Float(f, "RelativeHumidity", 0), Float(f, "FogLevel", 0)),
        };
    }

    private static int Int(TelemetryFrame f, string name, int idx, int fallback) =>
        f.Get(name, idx) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : fallback;

    private static float Float(TelemetryFrame f, string name, int idx) =>
        f.Get(name, idx) is { } v ? Convert.ToSingle(v, CultureInfo.InvariantCulture) : -1f;

    private static double Double(TelemetryFrame f, string name) =>
        f.Get(name, 0) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : -1;

    private static uint UInt(TelemetryFrame f, string name, int idx) =>
        f.Get(name, idx) is { } v ? Convert.ToUInt32(v, CultureInfo.InvariantCulture) : 0u;
}
