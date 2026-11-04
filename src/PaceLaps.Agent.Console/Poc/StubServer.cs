using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>
/// In-process stand-in for the web service (proof of concept; the real link is roadmap step 3). It accepts the
/// session, answers clock pings with this PC's clock (plus an optional offset), makes the agent the executor,
/// prints what matters (not snapshots or heartbeats), and writes every message in both directions to a JSON Lines file. The operator's name
/// in hello is redacted in the file.
/// </summary>
internal sealed class StubServer : IServerLink, IDisposable
{
    private readonly IClock _clock;
    private readonly StreamWriter _outbox;
    private readonly TimeSpan _offset;
    private readonly int? _registeredLeague;
    private readonly bool _executor;
    private readonly object _gate = new();
    private int _snapshots;

    /// <param name="registeredLeague">Only this league counts as registered; null accepts any league.</param>
    public StubServer(string outboxPath, IClock clock, TimeSpan offset, int? registeredLeague, bool executor)
    {
        _clock = clock;
        _offset = offset;
        _registeredLeague = registeredLeague;
        _executor = executor;
        Directory.CreateDirectory(Path.GetDirectoryName(outboxPath)!);
        _outbox = new StreamWriter(outboxPath, append: false, new System.Text.UTF8Encoding(false));
        OutboxPath = outboxPath;
    }

    public string OutboxPath { get; }

    public event Action<CommandMessage>? CommandReceived;

    public event Action<ServerView>? ViewReceived;

    public event Action<long>? EventsAcknowledged;

    // The stub never goes away, so there is nothing to resync.
    public event Action? Reconnected
    {
        add { }
        remove { }
    }

    public int SnapshotCount => _snapshots;

    public Task<ClockPongMessage?> PingAsync(ClockPingMessage ping, CancellationToken ct)
    {
        Write("out", "clock_ping", ping);
        var pong = new ClockPongMessage { ClientSentAt = ping.ClientSentAt, ServerTime = _clock.UtcNow + _offset };
        Write("in", "clock_pong", pong);
        return Task.FromResult<ClockPongMessage?>(pong);
    }

    public Task<SessionCheck> CheckSessionAsync(SessionIdentity identity, int operatorCustId, CancellationToken ct)
    {
        var registered = _registeredLeague is null || _registeredLeague == identity.LeagueId;
        var check = new SessionCheck(registered, true, registered ? null : $"stub server: only league {_registeredLeague} is registered");
        Write("in", "session_check", new { identity, check });
        Console.WriteLine($"[stub] session check league {identity.LeagueId}: {(check.Ok ? "registered, operator is a steward" : check.Reason)}");
        return Task.FromResult(check);
    }

    public Task<ServerView> HelloAsync(HelloMessage hello, CancellationToken ct)
    {
        Send(hello);
        var view = new ServerView(true, "stub", "Stub session", 1, _executor);
        Write("in", "hello_result", view);
        Console.WriteLine($"[stub] bound to sub-session {hello.Session?.SubSessionId}; executor {_executor} (epoch 1)");
        return Task.FromResult(view);
    }

    /// <summary>Tells the agent a new executor epoch, as the server's executor_assignment would.</summary>
    public void Assign(long epoch, bool isExecutor) => ViewReceived?.Invoke(new ServerView(null, "stub", null, epoch, isExecutor));

    public void Send(object message)
    {
        switch (message)
        {
            case HelloMessage hello:
                Write("out", "hello", hello with { Operator = hello.Operator is { } o ? o with { Name = o.Name is null ? null : $"(redacted, {o.Name.Length} characters)" } : null });
                Console.WriteLine($"[stub] hello: {hello.State}, channels macro {hello.Channels?.Macro}, typed {hello.Channels?.Typed}, sdk {hello.Channels?.Sdk}");
                break;
            case SnapshotMessage snapshot:
                Write("out", "snapshot", snapshot);
                // Not printed: at 2 Hz they broke up the operator's typing (acceptance run). Flag changes print as
                // events, and 'status' shows the count.
                Interlocked.Increment(ref _snapshots);

                break;
            case AgentEventMessage e:
                Write("out", "event", e);
                Console.WriteLine($"[stub] event {e.Seq} {e.EventType}: {JsonSerializer.Serialize(e.Data, ContractJson.Options)}");
                EventsAcknowledged?.Invoke(e.Seq);
                break;
            case CommandStatusMessage s:
                Write("out", "command_status", s);
                Console.WriteLine($"[stub] {s.CommandId} {s.Step}{(s.Outcome is { } outcome ? " " + outcome : "")}{(s.Detail is null ? "" : ": " + s.Detail)}");
                break;
            case HeartbeatMessage h:
                Write("out", "heartbeat", h);
                break;
            case SelfTestResultMessage r:
                Write("out", "self_test_result", r);
                Console.WriteLine($"[stub] self-test {(r.Passed ? "passed" : "failed")}");
                break;
            default:
                Write("out", message.GetType().Name, message);
                break;
        }
    }

    /// <summary>Delivers a command as if the server had sent it.</summary>
    public void Deliver(CommandMessage command)
    {
        Write("in", "command", command);
        CommandReceived?.Invoke(command);
    }

    /// <summary>Records an operator action in the outbox, so the log shows what was done when.</summary>
    public void Note(string what, object? data = null) => Write("operator", what, data ?? new { });

    public void Dispose() => _outbox.Dispose();

    private void Write(string direction, string type, object message)
    {
        var line = JsonSerializer.Serialize(new { t = ContractJson.FormatTimestamp(_clock.UtcNow), dir = direction, type, message }, ContractJson.Options);
        lock (_gate)
        {
            _outbox.WriteLine(line);
            _outbox.Flush();
        }
    }
}
