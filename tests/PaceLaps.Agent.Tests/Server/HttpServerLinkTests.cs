using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Server;

/// <summary>A web service stand-in: answers by path, records requests, can be switched off.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    public ConcurrentQueue<(string Method, string Path, string Body, string? Auth)> Requests { get; } = new();

    public bool Down { get; set; }

    public Func<string, string, (HttpStatusCode, string)> Answer { get; set; } = (_, _) => (HttpStatusCode.NoContent, "");

    public IEnumerable<string> Bodies(string path) => Requests.Where(r => r.Path == path).Select(r => r.Body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue((request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
        if (Down)
        {
            throw new HttpRequestException("connection refused (fake)");
        }

        var (status, text) = Answer(path, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public sealed class HttpServerLinkTests
{
    private const string Heartbeat = """{"v":1,"kind":"heartbeat_result","state":"active","session":{"id":"12","name":"Race 1"},"epoch":3,"isExecutor":true}""";

    private readonly FakeApi _api = new();
    private readonly FakeClock _clock = new();
    private readonly HttpServerLink _link;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public HttpServerLinkTests()
    {
        _api.Answer = (path, body) => path switch
        {
            "/api/v1/agent/heartbeat" => (HttpStatusCode.OK, Heartbeat),
            "/api/v1/agent/events" => (HttpStatusCode.OK, $$"""{"v":1,"kind":"events_ack","lastAckedSeq":{{JsonDocument.Parse(body).RootElement.GetProperty("seq").GetInt64()}}}"""),
            "/api/v1/agent/resync" => (HttpStatusCode.OK, """{"v":1,"kind":"events_ack","lastAckedSeq":1005}"""),
            "/api/v1/agent/hello" => (HttpStatusCode.OK, ContractSchemas.Example("hello_result.waiting.json")),
            "/api/v1/agent/session-check" => (HttpStatusCode.OK, ContractSchemas.Example("session_check_result.json")),
            "/api/v1/agent/clock" => (HttpStatusCode.OK, ContractSchemas.Example("clock_pong.json")),
            "/api/v1/agent/commands" => (HttpStatusCode.OK, ContractSchemas.Example("commands.json")),
            _ => (HttpStatusCode.NoContent, ""),
        };
        _link = new HttpServerLink(new HttpServerLinkOptions { BaseUrl = new Uri("http://localhost:8081/"), AgentId = "agt_01", Token = "secret-token" },
            _clock, handler: _api);
    }

    private static AgentEventMessage Event(long seq) => new()
    {
        AgentId = "agt_01", Seq = seq, EventTime = DateTimeOffset.UnixEpoch, EventType = EventType.FlagChange,
        Data = new FlagChangeData(["caution"], ["green"], 0, 10),
    };

    private static CommandStatusMessage Status(string id) => new() { CommandId = id, Step = CommandStep.ReceivedByExecutor, At = DateTimeOffset.UnixEpoch };

    [Fact]
    public void Plain_http_is_only_for_this_pc()
    {
        Assert.Throws<ArgumentException>(() => new HttpServerLink(new HttpServerLinkOptions { BaseUrl = new Uri("http://race.example/"), AgentId = "a", Token = "t" }, _clock));
        _ = new HttpServerLink(new HttpServerLinkOptions { BaseUrl = new Uri("https://race.example/"), AgentId = "a", Token = "t" }, _clock);
    }

    [Fact]
    public async Task Heartbeat_results_become_the_servers_view_and_requests_carry_the_token()
    {
        var views = new List<ServerView>();
        _link.ViewReceived += views.Add;

        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 3, State = AgentState.Active }, _ct);

        Assert.Equal(new ServerView(true, "12", "Race 1", 3, true), Assert.Single(views));
        var request = Assert.Single(_api.Requests);
        Assert.Equal("Bearer secret-token", request.Auth);
        ContractSchemas.AssertValid("agent-messages.schema.json", request.Body);
    }

    [Fact]
    public async Task Hello_session_check_and_clock_follow_the_contract()
    {
        var view = await _link.HelloAsync(new HelloMessage
        {
            AgentId = "agt_01", State = AgentState.Ready, AgentVersion = "0.1.0", Session = new SessionChangeData(15457, 1, 0, 0, 0, "Practice"),
        }, _ct);
        var check = await _link.CheckSessionAsync(new SessionIdentity(15457, 1, 0, 0, 0, "Practice"), 100, _ct);
        var pong = await _link.PingAsync(new ClockPingMessage { ClientSentAt = _clock.UtcNow }, _ct);

        Assert.Equal(false, view.Active);
        Assert.Contains("no open session", view.Reason);
        Assert.True(check.Ok);
        Assert.NotNull(pong!.ServerReceivedAt);
        foreach (var body in _api.Bodies("/api/v1/agent/hello").Concat(_api.Bodies("/api/v1/agent/session-check")))
        {
            ContractSchemas.AssertValid("agent-messages.schema.json", body);
        }

        ContractSchemas.AssertValid("device-messages.schema.json", _api.Bodies("/api/v1/agent/clock").Single());
    }

    [Fact]
    public async Task Events_are_acknowledged()
    {
        var acks = new List<long>();
        _link.EventsAcknowledged += acks.Add;

        await _link.DeliverAsync(Event(1001), _ct);

        Assert.Equal([1001L], acks);
    }

    [Fact]
    public async Task A_self_test_result_goes_to_the_self_test_endpoint_once()
    {
        var result = new SelfTestResultMessage
        {
            AgentId = "agt_01", At = DateTimeOffset.UnixEpoch, Passed = true, Steps = [new SelfTestStepReport(1, SelfTestStepName.Sim, SelfTestStepResult.Pass)],
            Channels = new ChannelSet(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Disabled),
        };
        await _link.DeliverAsync(result, _ct);

        Assert.Contains("\"kind\":\"self_test_result\"", Assert.Single(_api.Bodies("/api/v1/agent/self-test")), StringComparison.Ordinal);

        // Refused (a server before step 5 has no such endpoint): logged, not repeated.
        _api.Answer = (_, _) => (System.Net.HttpStatusCode.NotFound, "");
        await _link.DeliverAsync(result, _ct);
        Assert.True(_link.IsOnline);
        Assert.Equal(2, _api.Bodies("/api/v1/agent/self-test").Count());
    }

    [Fact]
    public async Task The_outage_runs_from_the_first_failed_request_to_the_first_answered_one()
    {
        Assert.Null(_link.LastOutage);
        var start = _clock.UtcNow;
        _api.Down = true;
        await _link.DeliverAsync(Status("cmd_1"), _ct);
        _clock.Advance(TimeSpan.FromSeconds(12));
        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 3, State = AgentState.Active }, _ct);
        _clock.Advance(TimeSpan.FromSeconds(30));
        OutageWindow? atReconnect = null;
        _link.Reconnected += () => atReconnect = _link.LastOutage;

        _api.Down = false;
        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 3, State = AgentState.Active }, _ct);

        Assert.Equal(new OutageWindow(start, start.AddSeconds(42)), atReconnect);
    }

    [Fact]
    public async Task After_an_outage_status_reports_are_resent_and_the_agent_is_told_to_resync()
    {
        var reconnected = 0;
        _link.Reconnected += () => reconnected++;

        _api.Down = true;
        await _link.DeliverAsync(Status("cmd_1"), _ct);
        await _link.DeliverAsync(Event(1001), _ct);
        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 3, State = AgentState.Active }, _ct);
        Assert.False(_link.IsOnline);
        Assert.Equal(0, reconnected);

        _api.Down = false;
        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 3, State = AgentState.Active }, _ct);

        Assert.True(_link.IsOnline);
        Assert.Equal(1, reconnected);
        // The unsent status is queued again on the control lane; the event waits for the resync.
        _link.Send(new ResyncMessage { LastAckedSeq = 1000, Events = [Event(1001)] });
        await _link.DrainControlAsync(_ct);

        Assert.Equal(2, _api.Bodies("/api/v1/agent/command-status").Count());
        ContractSchemas.AssertValid("agent-messages.schema.json", _api.Bodies("/api/v1/agent/resync").Single());
    }

    [Fact]
    public async Task Refused_messages_are_not_an_outage()
    {
        _api.Answer = (_, _) => (HttpStatusCode.UnprocessableEntity, """{"message":"bad"}""");

        await _link.DeliverAsync(Status("cmd_1"), _ct);

        Assert.True(_link.IsOnline);
    }

    [Fact]
    public async Task Simulated_outage_fails_requests_until_it_ends()
    {
        _link.SimulateOutage(TimeSpan.FromSeconds(10));
        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 0, State = AgentState.Ready }, _ct);
        Assert.False(_link.IsOnline);
        Assert.Empty(_api.Requests);

        _clock.Advance(TimeSpan.FromSeconds(11));
        await _link.DeliverAsync(new HeartbeatMessage { AgentId = "agt_01", Epoch = 0, State = AgentState.Ready }, _ct);
        Assert.True(_link.IsOnline);
    }

    [Fact]
    public async Task The_long_poll_waits_a_second_when_an_answer_brings_nothing_new()
    {
        // The server keeps listing a command the agent already has (finding 3): one poll a second, not a loop.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var polls = 0;
        var answer = _api.Answer;
        _api.Answer = (path, body) =>
        {
            if (path == "/api/v1/agent/commands" && ++polls == 6)
            {
                stop.Cancel();
            }

            return answer(path, body);
        };

        await _link.LongPollAsync(stop.Token);

        // Five answers, the first new: four pauses of 1 s.
        Assert.Equal(TimeSpan.FromSeconds(4), _clock.Elapsed);
    }

    [Fact]
    public async Task Heartbeats_do_not_wait_behind_a_slow_snapshot()
    {
        // Finding 4: a snapshot held up by the server must not delay the heartbeat behind it.
        using var release = new ManualResetEventSlim();
        var answer = _api.Answer;
        _api.Answer = (path, body) =>
        {
            if (path == "/api/v1/agent/snapshot")
            {
                release.Wait(TimeSpan.FromSeconds(10));
            }

            return answer(path, body);
        };
        var views = new List<ServerView>();
        _link.ViewReceived += views.Add;
        _link.StartLanes();

        _link.Send(new SnapshotMessage
        {
            AgentId = "agt_01", Seq = 1, CapturedAt = _clock.UtcNow,
            Session = new SnapshotSession { LeagueId = 1, SeasonId = 1, SessionId = 1, SubSessionId = 1, Num = 0, Type = "Race", State = "Racing", TimeSec = 1, Flags = [] },
            Cars = [],
        });
        _link.Send(new HeartbeatMessage { AgentId = "agt_01", Epoch = 3, State = AgentState.Active });

        var until = DateTime.UtcNow.AddSeconds(5);
        while (views.Count == 0 && DateTime.UtcNow < until)
        {
            await Task.Delay(10, _ct);
        }

        Assert.Single(views);
        Assert.False(release.IsSet);
        release.Set();
        await _link.DisposeAsync();
    }

    [Fact]
    public async Task Long_poll_passes_each_command_on_once()
    {
        var commands = new List<CommandMessage>();
        _link.CommandReceived += commands.Add;

        Assert.Equal(HttpServerLink.PollResult.NewCommands, await _link.PollOnceAsync(_ct));
        // A repeat makes the loop wait before polling again (finding 3).
        Assert.Equal(HttpServerLink.PollResult.OnlyRepeats, await _link.PollOnceAsync(_ct));

        var expected = JsonDocument.Parse(ContractSchemas.Example("commands.json")).RootElement.GetProperty("commands")[0].GetProperty("id").GetString();
        Assert.Equal(expected, Assert.Single(commands).Id);
        Assert.Contains(_api.Requests, r => r.Method == "GET" && r.Path == "/api/v1/agent/commands");
    }
}

/// <summary>Frames to deliver, and what the client sent.</summary>
internal sealed class FakeTransport(params string?[] frames) : IWebSocketTransport
{
    private readonly Queue<string?> _frames = new(frames);

    public List<string> Sent { get; } = [];

    public Uri? Url { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct)
    {
        Url = url;
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        Sent.Add(text);
        return Task.CompletedTask;
    }

    public Task<string?> ReceiveAsync(CancellationToken ct) => Task.FromResult(_frames.Count > 0 ? _frames.Dequeue() : null);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class PusherSocketTests
{
    private readonly FakeClock _clock = new();

    [Fact]
    public async Task Subscribes_with_the_servers_signature_answers_pings_and_passes_channel_events_on()
    {
        var transport = new FakeTransport(
            """{"event":"pusher:connection_established","data":"{\"socket_id\":\"123.456\",\"activity_timeout\":30}"}""",
            """{"event":"pusher_internal:subscription_succeeded","channel":"private-agent.agt_01","data":"{}"}""",
            """{"event":"pusher:ping","data":{}}""",
            """{"event":"command","channel":"private-agent.agt_01","data":"{\"id\":\"cmd_1\"}"}""",
            """{"event":"command","channel":"private-agent.other","data":"{\"id\":\"cmd_2\"}"}""",
            null);
        string? authorized = null;
        var socket = new PusherSocket(new Uri("ws://localhost:8081/app/key?protocol=7"), "private-agent.agt_01",
            (id, channel, _) => { authorized = $"{id} {channel}"; return Task.FromResult("key:signature"); }, () => transport, _clock);
        var events = new List<(string, string)>();
        var subscribed = new List<bool>();
        socket.EventReceived += (n, d) => events.Add((n, d));
        socket.SubscriptionChanged += subscribed.Add;

        await socket.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("123.456 private-agent.agt_01", authorized);
        var subscribe = JsonDocument.Parse(transport.Sent[0]).RootElement;
        Assert.Equal("pusher:subscribe", subscribe.GetProperty("event").GetString());
        Assert.Equal("key:signature", subscribe.GetProperty("data").GetProperty("auth").GetString());
        Assert.Equal("""{"event":"pusher:pong","data":{}}""", transport.Sent[1]);
        Assert.Equal([("command", """{"id":"cmd_1"}""")], events);
        Assert.True(subscribed[0]);
    }

    [Fact]
    public async Task Pings_a_quiet_line_and_gives_up_without_an_answer()
    {
        var transport = new HangingTransport();
        var socket = new PusherSocket(new Uri("ws://localhost/app/k"), "private-agent.a", (_, _, _) => Task.FromResult("x"), () => transport, _clock);

        await socket.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["""{"event":"pusher:ping","data":{}}"""], transport.Sent);
        Assert.True(_clock.Elapsed >= TimeSpan.FromSeconds(60));
    }

    private sealed class HangingTransport : IWebSocketTransport
    {
        public List<string> Sent { get; } = [];

        public Task ConnectAsync(Uri url, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string text, CancellationToken ct)
        {
            Sent.Add(text);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveAsync(CancellationToken ct) => new TaskCompletionSource<string?>().Task;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class PairingClientTests
{
    [Fact]
    public async Task Polls_until_the_operator_approves()
    {
        var polls = 0;
        var api = new FakeApi
        {
            Answer = (path, _) => path switch
            {
                "/api/v1/agent/pairing" => (HttpStatusCode.OK, ContractSchemas.Example("pairing_code.json")),
                _ => ++polls < 3
                    ? (HttpStatusCode.OK, ContractSchemas.Example("pairing_status.json"))
                    : (HttpStatusCode.OK, ContractSchemas.Example("pairing_token.json")),
            },
        };
        var clock = new FakeClock();
        using var client = new PairingClient(new Uri("http://localhost:8081/"), clock, api);
        PairingCodeMessage? shown = null;

        var credentials = await client.PairAsync("0.1.0", c => shown = c, TestContext.Current.CancellationToken);

        Assert.Equal("KXQ7-M3RT", shown!.UserCode);
        Assert.Equal("agt_7f3k9q2m4x8w", credentials.AgentId);
        Assert.Equal(3, polls);
        Assert.Equal(TimeSpan.FromSeconds(15), clock.Elapsed);
        ContractSchemas.AssertValid("agent-pairing.schema.json", api.Bodies("/api/v1/agent/pairing").Single());
        ContractSchemas.AssertValid("agent-pairing.schema.json", api.Bodies("/api/v1/agent/pairing/token").First());
    }

    [Fact]
    public async Task Stops_when_denied()
    {
        var api = new FakeApi
        {
            Answer = (path, _) => path == "/api/v1/agent/pairing"
                ? (HttpStatusCode.OK, ContractSchemas.Example("pairing_code.json"))
                : (HttpStatusCode.OK, """{"v":1,"kind":"pairing_status","status":"denied"}"""),
        };
        using var client = new PairingClient(new Uri("http://localhost:8081/"), new FakeClock(), api);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PairAsync("0.1.0", _ => { }, TestContext.Current.CancellationToken));
    }
}

public sealed class ClockAndCredentialTests
{
    [Fact]
    public void The_ntp_formula_takes_the_servers_processing_time_out()
    {
        var estimator = new ClockOffsetEstimator();
        var sent = new DateTimeOffset(2026, 11, 7, 19, 0, 0, TimeSpan.Zero);
        // Server clock 500 ms ahead; 20 ms each way; the server took 100 ms to answer.
        estimator.Add(sent, sent.AddMilliseconds(520), sent.AddMilliseconds(620), sent.AddMilliseconds(140));

        Assert.Equal(500, estimator.Offset.TotalMilliseconds, 0.5);
        Assert.Equal(40, estimator.RoundTrip!.Value.TotalMilliseconds, 0.5);
    }

    [Fact]
    public void Credentials_round_trip_through_dpapi_for_this_user_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"irc-credentials-{Guid.NewGuid():N}.bin");
        try
        {
            var store = new PaceLaps.Agent.Hosting.CredentialStore(path);
            store.Save(new AgentCredentials(new Uri("https://race.example/"), "agt_01", "secret-token"));

            Assert.DoesNotContain("secret-token", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.Equal("secret-token", store.Load()!.Token);

            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.Null(store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
