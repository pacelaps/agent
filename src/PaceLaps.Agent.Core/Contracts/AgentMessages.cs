using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaceLaps.Agent.Core.Contracts;

/// <summary>The agent's states (spec: Agent specification &gt; States).</summary>
public enum AgentState
{
    Sleeping,
    SelfTest,
    Ready,
    Active,
}

/// <summary>Per-channel readiness. A channel that is not verified is never offered as ready.</summary>
public enum ChannelReadiness
{
    Disabled,
    Unverified,
    Ready,
    Failed,
}

public sealed record ChannelSet(ChannelReadiness Macro, ChannelReadiness Typed, ChannelReadiness Sdk);

public sealed record OperatorInfo(int? CustId, string? Name);

/// <summary>
/// Agent to server: hello. Sent when the agent becomes ready and whenever its session identity changes; the
/// answer (hello_result) says whether the server bound it to a session.
/// </summary>
public sealed record HelloMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "hello";
    public required string AgentId { get; init; }
    public required AgentState State { get; init; }
    public required string AgentVersion { get; init; }

    /// <summary>The session the sim is in (sessionIdentity); null when it is in none.</summary>
    public SessionChangeData? Session { get; init; }

    public OperatorInfo? Operator { get; init; }
    public ChannelSet? Channels { get; init; }
}

/// <summary>Agent to server: self-test step 2, is the league registered and the operator a steward?</summary>
public sealed record SessionCheckMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "session_check";
    public required string AgentId { get; init; }
    public required SessionChangeData Session { get; init; }
}

/// <summary>Agent to server: heartbeat, every second while ready or active.</summary>
public sealed record HeartbeatMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "heartbeat";
    public required string AgentId { get; init; }
    public required long Epoch { get; init; }
    public required AgentState State { get; init; }
    public bool? InSession { get; init; }
    public ChannelSet? Channels { get; init; }
    public int? LastCommandLatencyMs { get; init; }

    /// <summary>The agent's local failsafe: whether the operator turned it on, which version it holds, whether it runs.</summary>
    public LocalFailsafeState? LocalFailsafe { get; init; }
}

/// <summary>
/// The heartbeat's localFailsafe. Version and hash are written even when null: null means the agent holds no copy.
/// </summary>
public sealed record LocalFailsafeState
{
    public required bool Enabled { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Hash { get; init; }

    public required bool Running { get; init; }

    /// <summary>When it started; required while it runs.</summary>
    public DateTimeOffset? Since { get; init; }

    public static LocalFailsafeState None(bool enabled) => new() { Enabled = enabled, Running = false };
}

public enum EventType
{
    FlagChange,
    SessionChange,
    Incident,
    LocalFailsafe,
    SectorCrossings,
    SessionResults,
}

/// <summary>Agent to server: an event with a sequence number. Kept in the agent's buffer for resync.</summary>
public sealed record AgentEventMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "event";
    public required string AgentId { get; init; }
    public required long Seq { get; init; }
    public required DateTimeOffset EventTime { get; init; }
    public required EventType EventType { get; init; }
    public required object Data { get; init; }
}

/// <summary>Data of an incident event (shape fixed by the contract).</summary>
public sealed record IncidentData(string Car, int Delta, int Total, int SessionNum, double SessionTimeSec, int Lap, double LapDistPct);

/// <summary>Data of a sector crossings event: the crossings since the last one, bundled about once a second.</summary>
public sealed record SectorCrossingsData(int SessionNum, IReadOnlyList<SectorCrossingData> Crossings);

/// <summary>
/// A car crossed the start of <paramref name="Sector"/> at <paramref name="SessionTimeSec"/> (interpolated);
/// <paramref name="Lap"/> is its laps completed after it. Pit, Off and Jump say what happened since its previous
/// crossing; null when not (left out of the message).
/// </summary>
public sealed record SectorCrossingData(string Car, int Sector, int Lap, double SessionTimeSec, bool? Pit = null, bool? Off = null, bool? Jump = null);

/// <summary>Data of a session results event (spec-feedback 42): a session's results as iRacing has them.</summary>
public sealed record SessionResultsData(int SessionNum, bool Official, int LapsComplete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] FastestLapData? FastestLap, IReadOnlyList<ResultCarData> Cars);

/// <summary>The session's fastest lap: the car, its lap number and the time in seconds.</summary>
public sealed record FastestLapData(string Car, int Lap, double Time);

/// <summary>
/// A car's row: <paramref name="Gap"/> is seconds behind the leader (iRacing gives no race time), class position from
/// 1; lap times null while iRacing has none; <paramref name="Out"/> null while running. Nulls are written, as the
/// contract requires every field.
/// </summary>
public sealed record ResultCarData(string Car, int Position, int ClassPosition, int Laps, int LapsBehind, double Gap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? LastLap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? BestLap,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? BestLapNum,
    int LapsLed, int Incidents,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ResultOutData? Out);

/// <summary>Why a car is out: iRacing's ReasonOutId and ReasonOutStr.</summary>
public sealed record ResultOutData(int Id, string Text);

/// <summary>Data of a flag change event. Shape proposed by the agent; the contract leaves event data open.</summary>
public sealed record FlagChangeData(IReadOnlyList<string> Flags, IReadOnlyList<string> Previous, int SessionNum, double SessionTimeSec);

/// <summary>
/// A session identity (contract sessionIdentity): the data of a session change event, and the session in hello
/// and session_check.
/// </summary>
public sealed record SessionChangeData(int LeagueId, int SeasonId, int SessionId, int SubSessionId, int Num, string Type);

public enum LocalFailsafeAction
{
    Started,
    Command,
    Ended,
    Released,
}

/// <summary>Data of a local_failsafe event: the run, buffered through the outage like every event.</summary>
public sealed record LocalFailsafeEventData
{
    public required LocalFailsafeAction Action { get; init; }
    public required string Procedure { get; init; }
    public required int Version { get; init; }

    /// <summary>Action command: the command type and its result.</summary>
    public string? Command { get; init; }

    public CommandOutcome? Result { get; init; }
    public string? Detail { get; init; }
}

public enum CommandStep
{
    SentToServer,
    ReceivedByServer,
    SentToExecutor,
    ReceivedByExecutor,
    Executing,
    Result,
}

public enum CommandOutcome
{
    Sent,
    Verified,
    Failed,
}

/// <summary>Agent to server: one step of a command's status checklist.</summary>
public sealed record CommandStatusMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "command_status";
    public required string CommandId { get; init; }
    public required CommandStep Step { get; init; }
    public CommandOutcome? Outcome { get; init; }
    public required DateTimeOffset At { get; init; }

    /// <summary>Failure reason (required when failed) or a note, for example which channel was used.</summary>
    public string? Detail { get; init; }
}

/// <summary>Both ways: resync after a reconnect.</summary>
public sealed record ResyncMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "resync";
    public required long LastAckedSeq { get; init; }

    /// <summary>The outage as the agent saw it, for the server's audit log; null when it noticed none.</summary>
    public OutageWindow? Outage { get; init; }

    public IReadOnlyList<AgentEventMessage>? Events { get; init; }
}

/// <summary>From the first request that failed to the first one answered again (server-adjusted clock).</summary>
public sealed record OutageWindow(DateTimeOffset From, DateTimeOffset To);

public enum SelfTestStepName
{
    Sim,
    Session,
    Server,
    Cars,
    TypedChannel,
    MacroChannel,
}

public enum SelfTestStepResult
{
    Pass,
    Fail,
    Skipped,
    Unverified,
}

/// <summary>One step of a self-test result. The detail never carries names and is at most 200 characters.</summary>
public sealed record SelfTestStepReport(int Step, SelfTestStepName Name, SelfTestStepResult Result, string? Detail = null);

/// <summary>Agent to server: the result of a self-test that reached the server (POST self-test).</summary>
public sealed record SelfTestResultMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "self_test_result";
    public required string AgentId { get; init; }
    public required DateTimeOffset At { get; init; }
    public required bool Passed { get; init; }
    public required IReadOnlyList<SelfTestStepReport> Steps { get; init; }
    public required ChannelSet Channels { get; init; }
}

/// <summary>Agent to server: clock ping (device-messages schema).</summary>
public sealed record ClockPingMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "clock_ping";
    public required DateTimeOffset ClientSentAt { get; init; }
}

/// <summary>Server to agent: clock pong.</summary>
public sealed record ClockPongMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "clock_pong";
    public required DateTimeOffset ClientSentAt { get; init; }

    /// <summary>When the request reached the server (optional): with it the round trip excludes server time.</summary>
    public DateTimeOffset? ServerReceivedAt { get; init; }

    public required DateTimeOffset ServerTime { get; init; }
}

/// <summary>Server to agent: a structured command. Parameters are validated by the renderer, not here.</summary>
public sealed record CommandMessage
{
    public int V { get; init; } = ContractJson.Version;
    public required string Id { get; init; }
    public required long Epoch { get; init; }
    public required string Type { get; init; }
    public JsonElement Params { get; init; }
    public required bool Idempotent { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
    public DateTimeOffset? ExecuteAt { get; init; }
    public required string IssuedBy { get; init; }

    [JsonIgnore]
    public JsonElement ParamsOrEmpty => Params.ValueKind == JsonValueKind.Object ? Params : EmptyObject;

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
}
