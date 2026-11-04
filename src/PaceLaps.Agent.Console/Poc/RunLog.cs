using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>
/// The run's message log: every message in both directions as JSON Lines (docs/poc-logs). The operator's name in
/// hello is redacted; tokens never pass through here.
/// </summary>
internal sealed class RunLog : IDisposable
{
    private readonly IClock _clock;
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public RunLog(string path, IClock clock)
    {
        _clock = clock;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        Path = path;
    }

    public string Path { get; }

    public void Write(string direction, string type, object message)
    {
        if (message is HelloMessage hello)
        {
            message = hello with { Operator = hello.Operator is { } o ? o with { Name = o.Name is null ? null : $"(redacted, {o.Name.Length} characters)" } : null };
        }

        var line = JsonSerializer.Serialize(new { t = ContractJson.FormatTimestamp(_clock.UtcNow), dir = direction, type, message }, ContractJson.Options);
        lock (_gate)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    /// <summary>Records an operator action, so the log shows what was done when.</summary>
    public void Note(string what, object? data = null) => Write("operator", what, data ?? new { });

    public void Dispose() => _writer.Dispose();
}

/// <summary>Writes everything that passes through a server link to the run log and prints what matters.</summary>
internal sealed class RecordingLink : IServerLink
{
    private readonly IServerLink _inner;
    private readonly RunLog _log;
    private int _snapshots;

    public RecordingLink(IServerLink inner, RunLog log)
    {
        _inner = inner;
        _log = log;
        _inner.CommandReceived += c =>
        {
            _log.Write("in", "command", c);
            Console.WriteLine($"[server] command {c.Id} {c.Type} {c.ParamsOrEmpty.GetRawText()} epoch {c.Epoch}");
            CommandReceived?.Invoke(c);
        };
        _inner.ViewReceived += v =>
        {
            _log.Write("in", "view", v);
            ViewReceived?.Invoke(v);
        };
        _inner.EventsAcknowledged += seq =>
        {
            _log.Write("in", "events_ack", new { lastAckedSeq = seq });
            EventsAcknowledged?.Invoke(seq);
        };
        _inner.Reconnected += () =>
        {
            _log.Write("in", "reconnected", new { });
            Console.WriteLine("[server] reachable again: resync");
            Reconnected?.Invoke();
        };
    }

    public event Action<CommandMessage>? CommandReceived;

    public event Action<ServerView>? ViewReceived;

    public event Action<long>? EventsAcknowledged;

    public event Action? Reconnected;

    public int SnapshotCount => _snapshots;

    public async Task<ClockPongMessage?> PingAsync(ClockPingMessage ping, CancellationToken ct)
    {
        _log.Write("out", "clock_ping", ping);
        var pong = await _inner.PingAsync(ping, ct).ConfigureAwait(false);
        _log.Write("in", "clock_pong", (object?)pong ?? new { });
        return pong;
    }

    public async Task<SessionCheck> CheckSessionAsync(SessionIdentity identity, int operatorCustId, CancellationToken ct)
    {
        _log.Write("out", "session_check", identity);
        var check = await _inner.CheckSessionAsync(identity, operatorCustId, ct).ConfigureAwait(false);
        _log.Write("in", "session_check_result", check);
        Console.WriteLine($"[server] session check league {identity.LeagueId}: {(check.Ok ? "registered, operator is a steward" : check.Reason)}");
        return check;
    }

    public async Task<ServerView> HelloAsync(HelloMessage hello, CancellationToken ct)
    {
        _log.Write("out", "hello", hello);
        var view = await _inner.HelloAsync(hello, ct).ConfigureAwait(false);
        _log.Write("in", "hello_result", view);
        Console.WriteLine(view.Active == true
            ? $"[server] active in {view.SessionName}, epoch {view.Epoch}{(view.IsExecutor ? ", executor" : "")}"
            : $"[server] ready, not active: {view.Reason}");
        return view;
    }

    public void Send(object message)
    {
        switch (message)
        {
            case SnapshotMessage:
                Interlocked.Increment(ref _snapshots);
                break;
            case HeartbeatMessage:
                break;
            case AgentEventMessage e:
                _log.Write("out", "event", e);
                Console.WriteLine($"[agent] event {e.Seq} {e.EventType}: {JsonSerializer.Serialize(e.Data, ContractJson.Options)}");
                break;
            case CommandStatusMessage s:
                _log.Write("out", "command_status", s);
                Console.WriteLine($"[agent] {s.CommandId} {s.Step}{(s.Outcome is { } outcome ? " " + outcome : "")}{(s.Detail is null ? "" : ": " + s.Detail)}");
                break;
            case ResyncMessage r:
                _log.Write("out", "resync", r);
                Console.WriteLine($"[agent] resync: {r.Events?.Count ?? 0} events after {r.LastAckedSeq}");
                break;
            default:
                _log.Write("out", message.GetType().Name, message);
                break;
        }

        _inner.Send(message);
    }
}
