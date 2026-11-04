using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.Server;

public sealed record HttpServerLinkOptions
{
    /// <summary>The web service, for example https://race.example or http://localhost:8081 (plain HTTP only locally).</summary>
    public required Uri BaseUrl { get; init; }

    public required string AgentId { get; init; }

    /// <summary>The bearer token from pairing. Never logged.</summary>
    public required string Token { get; init; }

    /// <summary>The realtime server's public key; null reads it from the service's start page.</summary>
    public string? RealtimeKey { get; init; }

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long one long-poll waits on the server (it holds the request open).</summary>
    public int LongPollWaitSeconds { get; init; } = 20;
}

/// <summary>
/// The Agent API client (spec: Web service &gt; Agent API; contracts agent-messages and agent-server). Requests go
/// out on three lanes, so no kind of request waits behind another: the control lane (command status, events,
/// resync), the snapshot lane (only the newest few are kept) and the heartbeat lane (heartbeats queued behind slow
/// snapshots made the server count the agent lost; integration run 2026-10-26, finding 4). Commands
/// and executor assignments arrive on the websocket; while it is down, a long-poll asks for them. Every command is
/// passed on once, whichever way it came.
/// <para>
/// Outages: a failed request marks the link offline. Command status reports wait and are resent, events are left to
/// the resync, heartbeats and snapshots are dropped. The first answered request after an outage raises
/// <see cref="Reconnected"/>, so the agent resyncs its events.
/// </para>
/// </summary>
public sealed partial class HttpServerLink : IServerLink, IAsyncDisposable
{
    private const int RecentCommands = 500;

    private readonly HttpServerLinkOptions _options;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Func<IWebSocketTransport> _transports;
    private readonly Channel<object> _control = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<object> _heartbeats = Channel.CreateBounded<object>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Channel<object> _telemetry = Channel.CreateBounded<object>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });
    private readonly Queue<CommandStatusMessage> _unsentStatuses = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;
    private DateTimeOffset _offlineUntil = DateTimeOffset.MinValue;
    private bool _online = true;
    private DateTimeOffset _offlineSince;

    public HttpServerLink(HttpServerLinkOptions options, IClock clock, ILogger? logger = null, HttpMessageHandler? handler = null,
        Func<IWebSocketTransport>? transports = null)
    {
        var url = options.BaseUrl;
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && url.IsLoopback))
        {
            throw new ArgumentException($"The server must use HTTPS (plain HTTP only for localhost): {url}", nameof(options));
        }

        _options = options;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        _transports = transports ?? (() => new ClientWebSocketTransport());
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
        {
            BaseAddress = url,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public event Action<CommandMessage>? CommandReceived;

    public event Action<ServerView>? ViewReceived;

    public event Action<long>? EventsAcknowledged;

    public event Action? Reconnected;

    /// <summary>The last request was answered.</summary>
    public OutageWindow? LastOutage { get; private set; }

    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _online;
            }
        }
    }

    /// <summary>Commands arrive live on the websocket (otherwise by long-poll).</summary>
    public bool IsRealtime => Socket?.IsSubscribed == true;

    public PusherSocket? Socket { get; private set; }

    /// <summary>Starts the lanes, the websocket and the long-poll fallback.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var key = _options.RealtimeKey ?? await ReadRealtimeKeyAsync(ct).ConfigureAwait(false);
        var scheme = _options.BaseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var ws = new UriBuilder(_options.BaseUrl) { Scheme = scheme, Path = $"/app/{key}", Query = "protocol=7&client=pacelaps-agent&version=0.1&flash=false" }.Uri;
        Socket = new PusherSocket(ws, $"private-agent.{_options.AgentId}", AuthorizeChannelAsync, _transports, _clock, _logger)
        {
            Paused = SimulatingOutage,
        };
        Socket.EventReceived += OnRealtimeEvent;

        var stop = _stop.Token;
        StartLanes();
        _running = Task.WhenAll(_running, Task.Run(() => Socket.RunAsync(stop), stop), Task.Run(() => LongPollAsync(stop), stop));
    }

    /// <summary>Starts the request lanes only (StartAsync does it too). Internal for tests.</summary>
    internal void StartLanes()
    {
        var stop = _stop.Token;
        _running = Task.WhenAll(
            Task.Run(() => RunLaneAsync(_control.Reader, stop), stop),
            Task.Run(() => RunLaneAsync(_telemetry.Reader, stop), stop),
            Task.Run(() => RunLaneAsync(_heartbeats.Reader, stop), stop));
    }

    public async Task<ClockPongMessage?> PingAsync(ClockPingMessage ping, CancellationToken ct)
    {
        var pong = await PostAsync<ClockPongMessage>("clock", ping, ct).ConfigureAwait(false);
        return pong;
    }

    public async Task<SessionCheck> CheckSessionAsync(SessionIdentity identity, int operatorCustId, CancellationToken ct)
    {
        // The server knows the operator from the token; the customer id is not sent.
        var answer = await PostAsync<SessionCheckResultMessage>("session-check",
            new SessionCheckMessage { AgentId = _options.AgentId, Session = identity.ToContract() }, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("empty session check answer");
        return new SessionCheck(answer.LeagueRegistered, answer.OperatorIsSteward, answer.Reason);
    }

    public async Task<ServerView> HelloAsync(HelloMessage hello, CancellationToken ct)
    {
        var answer = await PostAsync<ServerViewMessage>("hello", hello, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("empty hello answer");
        return ServerView.From(answer);
    }

    public async Task<LocalFailsafeDocumentMessage?> FetchLocalFailsafeAsync(CancellationToken ct)
    {
        try
        {
            return await RequestAsync<LocalFailsafeDocumentMessage>(HttpMethod.Get, "api/v1/agent/local-failsafe", null, _options.RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (ServerRefusedException e) when (e.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Send(object message)
    {
        var lane = message switch
        {
            HeartbeatMessage => _heartbeats,
            SnapshotMessage => _telemetry,
            _ => _control,
        };
        if (message is SnapshotMessage && !IsOnline)
        {
            return;
        }

        lane.Writer.TryWrite(message);
    }

    public async Task<bool> SendNowAsync(object message, CancellationToken ct)
    {
        try
        {
            await DeliverAsync(message, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or ServerRefusedException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Not delivered: {Kind} ({Reason})", message.GetType().Name, e.Message);
            return false;
        }
    }

    /// <summary>The console's <c>offline</c> command: requests fail and the websocket closes for this long.</summary>
    public void SimulateOutage(TimeSpan duration)
    {
        lock (_gate)
        {
            _offlineUntil = _clock.UtcNow + duration;
        }

        _logger.LogWarning("Simulated outage for {Seconds} s", duration.TotalSeconds);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _control.Writer.TryComplete();
        _telemetry.Writer.TryComplete();
        _heartbeats.Writer.TryComplete();
        try
        {
            await _running.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        _http.Dispose();
        _stop.Dispose();
    }

    /// <summary>Sends one message from a lane. Internal for tests.</summary>
    internal async Task DeliverAsync(object message, CancellationToken ct)
    {
        try
        {
            switch (message)
            {
                case HeartbeatMessage heartbeat:
                    if (await PostAsync<ServerViewMessage>("heartbeat", heartbeat, ct).ConfigureAwait(false) is { } view)
                    {
                        ViewReceived?.Invoke(ServerView.From(view));
                    }

                    break;
                case SnapshotMessage snapshot:
                    await PostAsync<object>("snapshot", snapshot, ct).ConfigureAwait(false);
                    break;
                case AgentEventMessage e:
                    Acknowledge(await PostAsync<EventsAckMessage>("events", e, ct).ConfigureAwait(false));
                    break;
                case ResyncMessage resync:
                    Acknowledge(await PostAsync<EventsAckMessage>("resync", resync, ct).ConfigureAwait(false));
                    break;
                case CommandStatusMessage status:
                    await PostAsync<object>("command-status", status, ct).ConfigureAwait(false);
                    break;
                case SelfTestResultMessage result:
                    // Sent once: a self-test the server cannot take now is not repeated (the next one is reported).
                    await PostAsync<object>("self-test", result, ct).ConfigureAwait(false);
                    break;
                default:
                    _logger.LogWarning("No endpoint for {Type}", message.GetType().Name);
                    break;
            }
        }
        catch (ServerRefusedException e)
        {
            // Not an outage: the server read the message and refused it. Sending it again would not help.
            _logger.LogWarning("Server refused {Type}: {Status} {Detail}", message.GetType().Name, (int)e.Status, e.Detail);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            if (message is CommandStatusMessage status)
            {
                lock (_gate)
                {
                    _unsentStatuses.Enqueue(status);
                }
            }
        }
    }

    /// <summary>Sends what waits on the control lane now. Internal for tests (the lanes run on their own).</summary>
    internal async Task DrainControlAsync(CancellationToken ct)
    {
        while (_control.Reader.TryRead(out var message))
        {
            await DeliverAsync(message, ct).ConfigureAwait(false);
        }
    }

    private async Task RunLaneAsync(ChannelReader<object> lane, CancellationToken ct)
    {
        try
        {
            await foreach (var message in lane.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await DeliverAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    /// <summary>The long-poll fallback loop. Internal for tests.</summary>
    internal async Task LongPollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (IsRealtime || SimulatingOutage())
                {
                    await _clock.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                    continue;
                }

                var started = _clock.Elapsed;
                var result = await PollOnceAsync(ct).ConfigureAwait(false);
                if (result != PollResult.NewCommands && _clock.Elapsed - started < TimeSpan.FromSeconds(1))
                {
                    // Back at once with nothing new: the server still lists commands this agent already has
                    // (their status reports are on the way), or something cut the wait short. Asking again
                    // at once looped at 13 requests a second (integration run 2026-10-26, finding 3).
                    await _clock.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or ServerRefusedException)
            {
                await _clock.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    internal enum PollResult
    {
        Empty,
        NewCommands,
        OnlyRepeats,
    }

    /// <summary>One long-poll. Internal for tests.</summary>
    internal async Task<PollResult> PollOnceAsync(CancellationToken ct)
    {
        var wait = _options.LongPollWaitSeconds;
        var list = await RequestAsync<CommandListMessage>(HttpMethod.Get, $"api/v1/agent/commands?wait={wait}", null,
            _options.RequestTimeout + TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
        var commands = list?.Commands ?? [];
        var fresh = 0;
        foreach (var command in commands)
        {
            fresh += Deliver(command, "long-poll") ? 1 : 0;
        }

        return commands.Count == 0 ? PollResult.Empty : fresh > 0 ? PollResult.NewCommands : PollResult.OnlyRepeats;
    }

    private void OnRealtimeEvent(string name, string data)
    {
        try
        {
            switch (name)
            {
                case "command" when ContractJson.Deserialize<CommandMessage>(data) is { } command:
                    Deliver(command, "websocket");
                    break;
                case "executor_assignment" when ContractJson.Deserialize<ServerViewMessage>(data) is { } view:
                    ViewReceived?.Invoke(ServerView.From(view));
                    break;
            }
        }
        catch (JsonException e)
        {
            _logger.LogWarning("Realtime event {Name} is not valid: {Message}", name, e.Message);
        }
    }

    /// <summary>Passes a command on unless it was passed on before; true if it was new.</summary>
    private bool Deliver(CommandMessage command, string via)
    {
        lock (_gate)
        {
            if (!_seen.Add(command.Id))
            {
                return false;
            }

            _seenOrder.Enqueue(command.Id);
            if (_seenOrder.Count > RecentCommands)
            {
                _seen.Remove(_seenOrder.Dequeue());
            }
        }

        _logger.LogInformation("Command {Id} {Type} via {Via}", command.Id, command.Type, via);
        CommandReceived?.Invoke(command);
        return true;
    }

    private void Acknowledge(EventsAckMessage? ack)
    {
        if (ack is not null)
        {
            EventsAcknowledged?.Invoke(ack.LastAckedSeq);
        }
    }

    private async Task<string> AuthorizeChannelAsync(string socketId, string channel, CancellationToken ct)
    {
        using var body = new StringContent(JsonSerializer.Serialize(new { socket_id = socketId, channel_name = channel }), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        using var response = await _http.PostAsync("api/v1/agent/broadcasting/auth", body, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
        return doc.RootElement.GetProperty("auth").GetString() ?? throw new InvalidDataException("no auth in the channel authorization");
    }

    private async Task<string> ReadRealtimeKeyAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeout);
        var page = await _http.GetStringAsync("/", timeout.Token).ConfigureAwait(false);
        var match = RealtimeKeyMeta().Match(page);
        return match.Success ? match.Groups[1].Value : throw new InvalidDataException("the start page has no reverb-key meta tag; pass --realtime-key");
    }

    private Task<T?> PostAsync<T>(string path, object message, CancellationToken ct) where T : class =>
        RequestAsync<T>(HttpMethod.Post, $"api/v1/agent/{path}", message, _options.RequestTimeout, ct);

    private async Task<T?> RequestAsync<T>(HttpMethod method, string path, object? message, TimeSpan timeoutAfter, CancellationToken ct) where T : class
    {
        if (SimulatingOutage())
        {
            MarkOffline("offline (simulated)");
            throw new HttpRequestException("offline (simulated)");
        }

        using var request = new HttpRequestMessage(method, path);
        if (message is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(message, message.GetType(), ContractJson.Options), Encoding.UTF8, "application/json");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutAfter);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested)
        {
            MarkOffline(e.Message);
            throw;
        }

        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                MarkOffline($"HTTP {(int)response.StatusCode}");
                throw new HttpRequestException($"{path}: HTTP {(int)response.StatusCode}");
            }

            MarkOnline();
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new ServerRefusedException(response.StatusCode, text.Length > 300 ? text[..300] : text);
            }

            return response.StatusCode == HttpStatusCode.NoContent || text.Length == 0 || typeof(T) == typeof(object)
                ? null
                : JsonSerializer.Deserialize<T>(text, ContractJson.Options);
        }
    }

    private bool SimulatingOutage()
    {
        lock (_gate)
        {
            return _clock.UtcNow < _offlineUntil;
        }
    }

    private void MarkOffline(string why)
    {
        lock (_gate)
        {
            if (!_online)
            {
                return;
            }

            _online = false;
            _offlineSince = _clock.UtcNow;
        }

        _logger.LogWarning("Server unreachable: {Why}", why);
    }

    private void MarkOnline()
    {
        List<CommandStatusMessage> unsent;
        lock (_gate)
        {
            if (_online)
            {
                return;
            }

            _online = true;
            LastOutage = new OutageWindow(_offlineSince, _clock.UtcNow);
            unsent = [.. _unsentStatuses];
            _unsentStatuses.Clear();
        }

        _logger.LogInformation("Server reachable again; resending {Count} status reports and resyncing", unsent.Count);
        foreach (var status in unsent)
        {
            _control.Writer.TryWrite(status);
        }

        Reconnected?.Invoke();
    }

    [GeneratedRegex("name=\"reverb-key\" content=\"([^\"]+)\"")]
    private static partial Regex RealtimeKeyMeta();
}

/// <summary>The server answered with a 4xx status: it read the request and refused it.</summary>
public sealed class ServerRefusedException(HttpStatusCode status, string detail) : Exception($"HTTP {(int)status}: {detail}")
{
    public HttpStatusCode Status { get; } = status;

    public string Detail { get; } = detail;
}
