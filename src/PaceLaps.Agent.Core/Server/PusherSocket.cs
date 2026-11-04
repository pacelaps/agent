using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaceLaps.Agent.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.Server;

/// <summary>A text websocket, behind an interface so the protocol can be tested without a server.</summary>
public interface IWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next text frame, or null when the server closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
}

/// <summary>ClientWebSocket as an <see cref="IWebSocketTransport"/>.</summary>
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri url, CancellationToken ct) => _socket.ConnectAsync(url, ct);

    public Task SendAsync(string text, CancellationToken ct) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.ToArray());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                // Closing is best effort.
            }
        }

        _socket.Dispose();
    }
}

/// <summary>
/// A minimal Pusher protocol client (protocol 7, as Reverb speaks it) for one private channel: connect, authorize
/// the subscription through the web service, answer pings, ping when the line is quiet, and reconnect with backoff.
/// Events on the channel are passed on with their data (a JSON string in the Pusher protocol).
/// </summary>
public sealed class PusherSocket
{
    private readonly Uri _url;
    private readonly string _channel;
    private readonly Func<string, string, CancellationToken, Task<string>> _authorize;
    private readonly Func<IWebSocketTransport> _transports;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private volatile bool _subscribed;

    /// <param name="url">ws(s)://host/app/KEY?protocol=7...</param>
    /// <param name="channel">For example private-agent.agt_01.</param>
    /// <param name="authorize">(socket id, channel) to the auth signature, from the web service.</param>
    public PusherSocket(Uri url, string channel, Func<string, string, CancellationToken, Task<string>> authorize,
        Func<IWebSocketTransport> transports, IClock clock, ILogger? logger = null)
    {
        _url = url;
        _channel = channel;
        _authorize = authorize;
        _transports = transports;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Subscribed to the channel: events arrive live.</summary>
    public bool IsSubscribed => _subscribed;

    /// <summary>Wait before the next connection attempt; doubles up to 30 s, reset by a subscription.</summary>
    public TimeSpan Backoff { get; private set; } = TimeSpan.FromSeconds(1);

    /// <summary>An event on the channel: name and data (JSON text).</summary>
    public event Action<string, string>? EventReceived;

    /// <summary>Subscribed (true) or not (false).</summary>
    public event Action<bool>? SubscriptionChanged;

    /// <summary>Set while the link simulates an outage: no connection attempts.</summary>
    public Func<bool> Paused { get; init; } = () => false;

    /// <summary>Connects and reconnects until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Paused())
            {
                // A simulated outage is not a failing server: reconnect as soon as it ends.
                Backoff = TimeSpan.FromSeconds(1);
                try
                {
                    await _clock.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            {
                try
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogInformation("Websocket: {Message}", e.Message);
                }
                finally
                {
                    SetSubscribed(false);
                }
            }

            try
            {
                await _clock.Delay(Backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Backoff = TimeSpan.FromTicks(Math.Min(Backoff.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
        }
    }

    /// <summary>One connection, until it closes, goes quiet or the outage simulation pauses it. Public for tests.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var socket = _transports();
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await socket.ConnectAsync(_url, connection.Token).ConfigureAwait(false);

        var activity = TimeSpan.FromSeconds(30);
        var lastFrame = _clock.UtcNow;
        var pinged = false;
        var receive = socket.ReceiveAsync(connection.Token);
        while (true)
        {
            // Wake at least twice a second: for the quiet-line ping and the outage simulation.
            var tick = _clock.Delay(TimeSpan.FromMilliseconds(500), connection.Token);
            if (await Task.WhenAny(receive, tick).ConfigureAwait(false) == tick)
            {
                await tick.ConfigureAwait(false);
                if (Paused())
                {
                    _logger.LogInformation("Websocket closed (simulated outage)");
                    return;
                }

                if (_clock.UtcNow - lastFrame >= activity)
                {
                    if (pinged)
                    {
                        _logger.LogInformation("Websocket: no answer to ping, reconnecting");
                        return;
                    }

                    pinged = true;
                    lastFrame = _clock.UtcNow;
                    await socket.SendAsync("""{"event":"pusher:ping","data":{}}""", connection.Token).ConfigureAwait(false);
                }

                continue;
            }

            var frame = await receive.ConfigureAwait(false);
            if (frame is null)
            {
                _logger.LogInformation("Websocket closed by the server");
                return;
            }

            lastFrame = _clock.UtcNow;
            pinged = false;
            var timeout = await HandleAsync(socket, frame, connection.Token).ConfigureAwait(false);
            if (timeout is { } t)
            {
                activity = t;
            }

            receive = socket.ReceiveAsync(connection.Token);
        }
    }

    /// <summary>Handles one frame; returns the activity timeout when the connection is established.</summary>
    private async Task<TimeSpan?> HandleAsync(IWebSocketTransport socket, string frame, CancellationToken ct)
    {
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(frame) as JsonObject;
        }
        catch (JsonException)
        {
            _logger.LogWarning("Websocket: frame is not JSON");
            return null;
        }

        var name = message?["event"]?.GetValue<string>();
        var channel = message?["channel"]?.GetValue<string>();
        var data = message?["data"] switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonNode n => n.ToJsonString(),
            _ => "{}",
        };
        switch (name)
        {
            case "pusher:connection_established":
                var established = JsonNode.Parse(data);
                var socketId = established?["socket_id"]?.GetValue<string>() ?? throw new InvalidDataException("no socket_id");
                var auth = await _authorize(socketId, _channel, ct).ConfigureAwait(false);
                var subscribe = new JsonObject
                {
                    ["event"] = "pusher:subscribe",
                    ["data"] = new JsonObject { ["auth"] = auth, ["channel"] = _channel },
                };
                await socket.SendAsync(subscribe.ToJsonString(), ct).ConfigureAwait(false);
                return established?["activity_timeout"]?.GetValue<int>() is int seconds and > 0 ? TimeSpan.FromSeconds(seconds) : null;
            case "pusher_internal:subscription_succeeded":
                Backoff = TimeSpan.FromSeconds(1);
                SetSubscribed(true);
                return null;
            case "pusher:ping":
                await socket.SendAsync("""{"event":"pusher:pong","data":{}}""", ct).ConfigureAwait(false);
                return null;
            case "pusher:pong":
                return null;
            case "pusher:error":
                _logger.LogWarning("Websocket error from the server: {Data}", data);
                return null;
            case { } eventName when channel == _channel:
                EventReceived?.Invoke(eventName, data);
                return null;
            default:
                return null;
        }
    }

    private void SetSubscribed(bool subscribed)
    {
        if (_subscribed == subscribed)
        {
            return;
        }

        _subscribed = subscribed;
        _logger.LogInformation(subscribed ? "Websocket subscribed to {Channel}" : "Websocket lost {Channel}", _channel);
        SubscriptionChanged?.Invoke(subscribed);
    }
}
