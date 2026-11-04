namespace PaceLaps.Agent.Core.Contracts;

/// <summary>Agent to server: snapshot, 1 to 2 Hz. Not buffered: only the latest one matters.</summary>
public sealed record SnapshotMessage
{
    public int V { get; init; } = ContractJson.Version;
    public required string AgentId { get; init; }
    public required long Seq { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required SnapshotSession Session { get; init; }
    public required IReadOnlyList<SnapshotCar> Cars { get; init; }
}

public sealed record SnapshotSession
{
    public required int LeagueId { get; init; }
    public required int SeasonId { get; init; }
    public required int SessionId { get; init; }
    public required int SubSessionId { get; init; }
    public required int Num { get; init; }
    public required string Type { get; init; }
    public required string State { get; init; }
    public required double TimeSec { get; init; }

    /// <summary>Normalised flag names (contract vocabulary flagName).</summary>
    public required IReadOnlyList<string> Flags { get; init; }

    /// <summary>The layout being raced (race control's track map); left out when the sim does not name it.</summary>
    public SnapshotTrack? Track { get; init; }

    /// <summary>A lap-limited session's laps (race control's header); left out when unlimited.</summary>
    public SnapshotLaps? Laps { get; init; }

    /// <summary>A timed session's time (race control's header); left out when not limited by time.</summary>
    public SnapshotTime? Time { get; init; }

    /// <summary>The weather now (race control's weather card); left out when the sim does not report it.</summary>
    public SnapshotWeather? Weather { get; init; }

    /// <summary>The car classes with iRacing's name, colour and models (spec-feedback 44); left out without cars.</summary>
    public IReadOnlyList<SnapshotClass>? Classes { get; init; }
}

/// <summary>A car class as iRacing reports it: the server decides which name to show.</summary>
public sealed record SnapshotClass
{
    public required int Id { get; init; }
    public string? Name { get; init; }

    /// <summary>#rrggbb.</summary>
    public string? Colour { get; init; }
    public IReadOnlyList<string>? Models { get; init; }
}

public sealed record SnapshotLaps(int Total, int Remaining);

public sealed record SnapshotTime(double TotalSec, double RemainingSec);

public sealed record SnapshotWeather
{
    public required double AirTempC { get; init; }
    public required double TrackTempC { get; init; }
    public string? Wetness { get; init; }
    public double? PrecipitationPct { get; init; }
    public bool? DeclaredWet { get; init; }
    public string? Skies { get; init; }
    public double? WindSpeedMs { get; init; }
    public double? WindDirDeg { get; init; }
    public double? HumidityPct { get; init; }
    public double? FogPct { get; init; }
}

public sealed record SnapshotTrack
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string Config { get; init; }
    public double? LengthM { get; init; }

    /// <summary>SplitTimeInfo: lap distance of each sector start (race control's lap times); left out without.</summary>
    public IReadOnlyList<double>? SectorStarts { get; init; }
}

public sealed record SnapshotCar
{
    public required string Num { get; init; }
    public int? CustId { get; init; }
    public required int ClassId { get; init; }
    public required int Lap { get; init; }
    public required double LapDistPct { get; init; }
    public required bool OnPitRoad { get; init; }
    public required string Surface { get; init; }
    public required int PosClass { get; init; }
    public required int PosOverall { get; init; }
    public required bool IsPaceCar { get; init; }
    public int? Incidents { get; init; }
    public required bool Connected { get; init; }

    /// <summary>iRacing's time for the car's last lap, seconds; left out while it has none.</summary>
    public double? LastLapTime { get; init; }

    /// <summary>iRacing's best lap of the car in this session, seconds; left out without one.</summary>
    public double? BestLapTime { get; init; }
}
