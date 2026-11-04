using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaceLaps.Agent.Core.Contracts;

/// <summary>The server's session, as it names it to the agent.</summary>
public sealed record ServerSessionRef(string Id, string Name);

/// <summary>Server to agent: the answer to a session check (agent-server.schema.json).</summary>
public sealed record SessionCheckResultMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "session_check_result";
    public required bool LeagueRegistered { get; init; }
    public required bool OperatorIsSteward { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Server to agent: the server's view of the agent, as hello_result, heartbeat_result or executor_assignment
/// (agent-server.schema.json). executor_assignment carries sessionId instead of session and no state.
/// </summary>
public sealed record ServerViewMessage
{
    public int V { get; init; } = ContractJson.Version;
    public required string Kind { get; init; }
    public AgentState? State { get; init; }
    public ServerSessionRef? Session { get; init; }
    public string? SessionId { get; init; }
    public required long Epoch { get; init; }
    public required bool IsExecutor { get; init; }

    /// <summary>Why the agent is not active (hello_result only), for the operator.</summary>
    public string? Reason { get; init; }

    /// <summary>The league's macros policy; null from a server before step 5.</summary>
    public MacrosPolicy? Macros { get; init; }

    /// <summary>
    /// The local failsafe to hold, as sent: absent (a server before step 5), null (none designated) or an object.
    /// Read it through <see cref="LocalFailsafeKnown"/> and <see cref="LocalFailsafeTarget"/>.
    /// </summary>
    [JsonPropertyName("localFailsafe")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement LocalFailsafeRaw { get; init; }

    /// <summary>The server said which local failsafe to hold (possibly none).</summary>
    [JsonIgnore]
    public bool LocalFailsafeKnown => LocalFailsafeRaw.ValueKind is JsonValueKind.Object or JsonValueKind.Null;

    [JsonIgnore]
    public LocalFailsafeTarget? LocalFailsafeTarget =>
        LocalFailsafeRaw.ValueKind == JsonValueKind.Object ? LocalFailsafeRaw.Deserialize<LocalFailsafeTarget>(ContractJson.Options) : null;

    /// <summary>heartbeat_result: the agent's place in the local failsafe's stagger (the executor is 0).</summary>
    public int? LocalFailsafeRank { get; init; }

    /// <summary>heartbeat_result: a steward released the local failsafe this agent runs.</summary>
    public bool? ReleaseLocalFailsafe { get; init; }
}

public enum MacrosPolicy
{
    Allowed,
    Off,
}

/// <summary>The local failsafe the server tells the agent to hold (localFailsafeTarget).</summary>
public sealed record LocalFailsafeTarget(string Procedure, int Version, string Hash, bool ArmPractice, bool ArmQualify);

/// <summary>Server to agent: GET local-failsafe, the procedure document with its version and hash.</summary>
public sealed record LocalFailsafeDocumentMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "local_failsafe";
    public required string Procedure { get; init; }
    public required int Version { get; init; }
    public required string Hash { get; init; }

    /// <summary>The procedure (procedure.schema.json), offline blocks only.</summary>
    public required JsonElement Document { get; init; }
}

/// <summary>Server to agent: the agent may drop its buffered events up to this sequence number.</summary>
public sealed record EventsAckMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "events_ack";
    public required long LastAckedSeq { get; init; }
}

/// <summary>Server to agent: the long-poll answer, commands not acknowledged yet.</summary>
public sealed record CommandListMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "commands";
    public required IReadOnlyList<CommandMessage> Commands { get; init; }
}

/// <summary>Agent to server: start device-code pairing (agent-pairing.schema.json).</summary>
public sealed record PairingRequestMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "pairing_request";
    public required string AgentVersion { get; init; }
}

/// <summary>Server to agent: the code the operator approves in the browser.</summary>
public sealed record PairingCodeMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "pairing_code";
    public required string PairingId { get; init; }
    public required string UserCode { get; init; }
    public required string VerificationUrl { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public required int IntervalSec { get; init; }
}

/// <summary>Agent to server: is the pairing approved yet?</summary>
public sealed record PairingPollMessage
{
    public int V { get; init; } = ContractJson.Version;
    public string Kind { get; init; } = "pairing_poll";
    public required string PairingId { get; init; }
}

/// <summary>Server to agent: pairing_status (pending, slow_down, denied, expired) or pairing_token.</summary>
public sealed record PairingAnswerMessage
{
    public int V { get; init; } = ContractJson.Version;
    public required string Kind { get; init; }
    public string? Status { get; init; }
    public int? IntervalSec { get; init; }
    public string? AgentId { get; init; }
    public string? Token { get; init; }
}
