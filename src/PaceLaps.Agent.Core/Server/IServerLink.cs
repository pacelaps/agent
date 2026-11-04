using PaceLaps.Agent.Core.Contracts;

namespace PaceLaps.Agent.Core.Server;

/// <summary>The session identity the agent reports, and the server checks against its registered leagues.</summary>
public sealed record SessionIdentity(int LeagueId, int SeasonId, int SessionId, int SubSessionId, int SessionNum, string SessionType)
{
    /// <summary>The contract's sessionIdentity.</summary>
    public SessionChangeData ToContract() => new(LeagueId, SeasonId, SessionId, SubSessionId, SessionNum, SessionType);
}

/// <summary>The server's answer to a session check (self-test step 2).</summary>
public sealed record SessionCheck(bool LeagueRegistered, bool OperatorIsSteward, string? Reason = null)
{
    public bool Ok => LeagueRegistered && OperatorIsSteward;
}

/// <summary>
/// The server's view of this agent (hello_result, heartbeat_result, executor_assignment): whether it is bound to a
/// session, the executor epoch and whether it is the executor. A null <see cref="Active"/> leaves the state as it is
/// (an executor assignment says nothing about it).
/// Since step 5 hello and heartbeat results also carry the league's macros policy and the local failsafe to hold,
/// and heartbeat results its stagger rank and a steward's release; null where the answer did not say.
/// </summary>
public sealed record ServerView(bool? Active, string? SessionId, string? SessionName, long Epoch, bool IsExecutor, string? Reason = null)
{
    public MacrosPolicy? Macros { get; init; }

    /// <summary>The server said which local failsafe to hold; <see cref="LocalFailsafe"/> null then means none.</summary>
    public bool LocalFailsafeKnown { get; init; }

    public LocalFailsafeTarget? LocalFailsafe { get; init; }

    public int? LocalFailsafeRank { get; init; }

    public bool ReleaseLocalFailsafe { get; init; }

    public static ServerView From(ServerViewMessage m) => new(
        m.Kind == "executor_assignment" ? null : m.State == AgentState.Active,
        m.Session?.Id ?? m.SessionId,
        m.Session?.Name,
        m.Epoch,
        m.IsExecutor,
        m.Reason)
    {
        Macros = m.Macros,
        LocalFailsafeKnown = m.LocalFailsafeKnown,
        LocalFailsafe = m.LocalFailsafeTarget,
        LocalFailsafeRank = m.LocalFailsafeRank,
        ReleaseLocalFailsafe = m.ReleaseLocalFailsafe == true,
    };
}

/// <summary>
/// The agent's connection to the web service: <c>HttpServerLink</c> (Agent API over HTTPS, commands on the
/// websocket with a long-poll fallback), or the proof of concept's in-process stub.
/// </summary>
public interface IServerLink
{
    Task<ClockPongMessage?> PingAsync(ClockPingMessage ping, CancellationToken ct);

    Task<SessionCheck> CheckSessionAsync(SessionIdentity identity, int operatorCustId, CancellationToken ct);

    /// <summary>Sends hello; the answer says whether the server bound the agent to a session (spec: States).</summary>
    Task<ServerView> HelloAsync(HelloMessage hello, CancellationToken ct);

    /// <summary>Sends a message (snapshot, event, heartbeat, command status, resync). Never blocks.</summary>
    void Send(object message);

    /// <summary>Sends at once and waits until the server took it (the last heartbeat before Sleeping); false if not.</summary>
    Task<bool> SendNowAsync(object message, CancellationToken ct)
    {
        Send(message);
        return Task.FromResult(true);
    }

    /// <summary>A command from the server for this agent.</summary>
    event Action<CommandMessage>? CommandReceived;

    /// <summary>The server's view changed or was repeated (heartbeat results, executor assignments).</summary>
    event Action<ServerView>? ViewReceived;

    /// <summary>The server has every event up to this sequence number.</summary>
    event Action<long>? EventsAcknowledged;

    /// <summary>The server is reachable again after an outage: the agent resyncs its events.</summary>
    event Action? Reconnected;

    /// <summary>
    /// The last outage on this PC's clock: from the first request that failed to the first one answered again. Set
    /// before <see cref="Reconnected"/>; null when there was none (or the link cannot tell).
    /// </summary>
    OutageWindow? LastOutage => null;

    /// <summary>GET local-failsafe: the procedure the agent should hold; null when the league designates none.</summary>
    Task<LocalFailsafeDocumentMessage?> FetchLocalFailsafeAsync(CancellationToken ct) => Task.FromResult<LocalFailsafeDocumentMessage?>(null);
}
