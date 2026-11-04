using System.Globalization;
using System.Text.RegularExpressions;

namespace PaceLaps.Agent.Core.Sim;

/// <summary>One entry of the session's driver list.</summary>
/// <param name="Incidents">The car's incident count as other drivers see it (session info), if known.</param>
/// <param name="ClassName">CarClassShortName (empty in AI sessions, class names check 2026-11-04).</param>
/// <param name="ClassColour">CarClassColor as the session info gives it, 0xrrggbb.</param>
/// <param name="Model">CarScreenName, the car model.</param>
public sealed record SimCar(int CarIdx, string Number, int ClassId, bool IsPaceCar, bool IsSpectator, int CustId, int? Incidents,
    string? ClassName = null, string? ClassColour = null, string? Model = null);

/// <summary>The layout being raced (session info WeekendInfo). Id is iRacing's TrackID, one per layout.</summary>
public sealed record SimTrack(int Id, string Name, string Config, double? LengthM)
{
    /// <summary>SplitTimeInfo: where the layout's sectors start, as lap distance (0 to 1), in sector order.</summary>
    public IReadOnlyList<double> SectorStarts { get; init; } = [];

    /// <summary>The track from the session info's fields; null without a track id or name.</summary>
    public static SimTrack? From(int id, string? displayName, string? configName, string? length) =>
        id > 0 && !string.IsNullOrWhiteSpace(displayName)
            ? new SimTrack(id, displayName.Trim(), configName?.Trim() ?? "", LengthInMetres(length))
            : null;

    /// <summary>"4.30 km" (or "2.67 mi", "800 m") in metres; null when it cannot be read.</summary>
    public static double? LengthInMetres(string? length)
    {
        var match = Regex.Match(length ?? "", @"^\s*([\d.]+)\s*(km|mi|m)\s*$",
            RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        return Math.Round(match.Groups[2].Value.ToLowerInvariant() switch
        {
            "km" => value * 1000,
            "mi" => value * 1609.344,
            _ => value,
        }, 1);
    }
}

/// <summary>The session info the agent needs, in iRacing-independent form. Changes rarely.</summary>
public sealed record SimSession
{
    public required int UpdateCount { get; init; }
    public string? BuildVersion { get; init; }
    public required int LeagueId { get; init; }
    public required int SeasonId { get; init; }
    public required int SessionId { get; init; }
    public required int SubSessionId { get; init; }
    public required int SessionNum { get; init; }
    public required string SessionType { get; init; }
    public required int DriverCarIdx { get; init; }
    public required bool DriverIsAdmin { get; init; }
    public int OperatorCustId { get; init; }
    public string? OperatorName { get; init; }
    public required int PaceCarIdx { get; init; }
    public required IReadOnlyList<SimCar> Cars { get; init; }

    /// <summary>The layout being raced, if the session info names it.</summary>
    public SimTrack? Track { get; init; }

    /// <summary>Each session's results as iRacing has them (race control's results, spec-feedback 42).</summary>
    public IReadOnlyList<SimSessionResults> Results { get; init; } = [];

    public SimCar? CarByIdx(int idx) => Cars.FirstOrDefault(c => c.CarIdx == idx);

    public SimCar? CarByNumber(string number) => Cars.FirstOrDefault(c => c.Number == number && !c.IsSpectator);
}

/// <summary>Live per-car values from telemetry. Lap times in seconds, -1 while iRacing has none.</summary>
public readonly record struct SimCarState(
    int Lap,
    int LapCompleted,
    float LapDistPct,
    bool OnPitRoad,
    int Surface,
    int Position,
    int ClassPosition,
    uint SessionFlags,
    uint PaceFlags,
    float LastLapTime = -1f,
    float BestLapTime = -1f);

/// <summary>
/// The session's length now, as iRacing gives it (SessionLapsTotal, SessionLapsRemainEx, SessionTimeTotal,
/// SessionTimeRemain): 32767 laps means unlimited; time has no single unlimited value (session and weather check,
/// 2026-11-03).
/// </summary>
public sealed record SimSessionLength(int LapsTotal, int LapsRemainEx, double TimeTotal, double TimeRemain);

/// <summary>
/// The weather now (AirTemp, TrackTempCrew, TrackWetness 0 unknown to 7 extremely wet, Precipitation 0 to 1,
/// WeatherDeclaredWet, Skies 0 clear to 3 overcast, WindVel m/s, WindDir radians, RelativeHumidity and FogLevel 0 to 1).
/// </summary>
public sealed record SimWeather(float AirTemp, float TrackTemp, int Wetness, float Precipitation, bool DeclaredWet, int Skies,
    float WindVel, float WindDir, float Humidity, float Fog);

/// <summary>One telemetry frame, in iRacing-independent form. <see cref="Cars"/> is indexed by car index.</summary>
public sealed record SimFrame
{
    public required TimeSpan Captured { get; init; }
    public required int Tick { get; init; }
    public required double SessionTime { get; init; }
    public required int SessionNum { get; init; }
    public required int SessionState { get; init; }
    public required uint SessionFlags { get; init; }
    public required int PaceMode { get; init; }
    public required IReadOnlyList<SimCarState> Cars { get; init; }

    /// <summary>The session's length, when the sim reports it.</summary>
    public SimSessionLength? Length { get; init; }

    /// <summary>The weather, when the sim reports it.</summary>
    public SimWeather? Weather { get; init; }

    public SimCarState? Car(int idx) => idx >= 0 && idx < Cars.Count ? Cars[idx] : null;
}

/// <summary>The sim as the agent's logic sees it. Implemented over IRSDKSharper in the IRacing project.</summary>
public interface ISimData
{
    bool IsConnected { get; }

    SimSession? Session { get; }

    SimFrame? Frame { get; }
}

/// <summary>
/// Every telemetry frame as it arrives (about 60 Hz), on the sim's thread: for what the latest frame is too coarse
/// for, such as timing sector crossings. Implemented by the iRacing adapter.
/// </summary>
public interface ISimFrames
{
    event Action<SimFrame>? FrameArrived;
}

/// <summary>iRacing's enum and flag values, as the SDK defines them.</summary>
public static class SimValues
{
    public const uint FlagCheckered = 0x00000001;
    public const uint FlagWhite = 0x00000002;
    public const uint FlagGreen = 0x00000004;
    public const uint FlagYellow = 0x00000008;
    public const uint FlagRed = 0x00000010;
    public const uint FlagDebris = 0x00000040;
    public const uint FlagYellowWaving = 0x00000100;
    public const uint FlagOneLapToGreen = 0x00000200;
    public const uint FlagGreenHeld = 0x00000400;
    public const uint FlagTenToGo = 0x00000800;
    public const uint FlagFiveToGo = 0x00001000;
    public const uint FlagCaution = 0x00004000;
    public const uint FlagCautionWaving = 0x00008000;
    public const uint FlagBlack = 0x00010000;

    public const uint PaceEndOfLine = 0x01;
    public const uint PaceWavedAround = 0x04;

    public const int SurfaceNotInWorld = -1;
    public const int SurfaceOffTrack = 0;
    public const int SurfaceOnTrack = 3;

    public const int StateRacing = 4;
    public const int StateCheckered = 5;

    public const int PaceModeSingleFileRestart = 2;
    public const int PaceModeDoubleFileRestart = 3;

    public static string SessionStateName(int state) => state switch
    {
        0 => "Invalid",
        1 => "GetInCar",
        2 => "Warmup",
        3 => "ParadeLaps",
        4 => "Racing",
        5 => "Checkered",
        6 => "CoolDown",
        _ => "Unknown",
    };

    public static string SurfaceName(int surface) => surface switch
    {
        -1 => "NotInWorld",
        0 => "OffTrack",
        1 => "InPitStall",
        2 => "ApproachingPits",
        3 => "OnTrack",
        _ => "Unknown",
    };
}
