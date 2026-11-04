using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Hosting;

/// <summary>The connection the runtime opens, over the fake server.</summary>
internal sealed class FakeConnection(FakeServer server) : IServerConnection
{
    public IServerLink Link => server;

    public Exception? FailStart { get; set; }

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public bool IsOnline => Started && !Disposed;

    public bool IsRealtime => false;

    public Task StartAsync(CancellationToken ct) => FailStart is { } e ? Task.FromException(e) : Task.FromResult(Started = true);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

public sealed class AgentRuntimeTests : IDisposable
{
    private static readonly Uri Server = new("https://race.example/");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "irc-runtime-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();
    private readonly FakeSim _sim = new();
    private readonly FakeServer _server;
    private readonly ConfigStore _config;
    private readonly CredentialStore _credentials;
    private readonly List<FakeConnection> _connections = [];
    private readonly AgentRuntime _runtime;
    private readonly TypedChatSender _typed;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public AgentRuntimeTests()
    {
        _server = new FakeServer(_clock);
        _config = new ConfigStore(Path.Combine(_dir, "config.json"));
        _credentials = new CredentialStore(Path.Combine(_dir, "credentials.bin"));
        var desktop = new FakeDesktop(_clock);
        _typed = new TypedChatSender(desktop, desktop, desktop, _clock, poster: desktop);
        var parts = new RuntimeParts
        {
            Sim = _sim,
            Typed = _typed,
            MacroConfig = _ => new NoMacroConfig(),
            Prompt = new FakePrompt(),
            Connect = _ =>
            {
                var c = new FakeConnection(_server);
                _connections.Add(c);
                return c;
            },
            LocalFailsafeStore = new MemoryLocalFailsafeStore(),
            Clock = _clock,
            RunLoop = false,
        };
        _runtime = new AgentRuntime(parts, _config, _credentials, "0.1.0-test");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class NoMacroConfig : Core.SelfTest.IMacroConfigSource
    {
        public IReadOnlyDictionary<int, string>? ReadSlots() => null;

        public string Location => "none (test)";
    }

    private void Pair(Uri? server = null) => _credentials.Save(new AgentCredentials(server ?? Server, "agt_01", "secret"));

    [Fact]
    public async Task Sleeping_there_is_no_connection_and_an_unpaired_pc_is_told_to_pair_first()
    {
        Assert.Equal(AgentState.Sleeping, _runtime.State);

        Assert.Null(await _runtime.ActivateAsync(_ct));

        Assert.Empty(_connections);
        Assert.Contains("Not paired", _runtime.Problem, StringComparison.Ordinal);
        Assert.Contains(_runtime.Status().Describe(), l => l.StartsWith("Problem: Not paired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Activate_opens_the_connection_runs_the_self_test_and_sleep_closes_it()
    {
        Pair();

        var report = await _runtime.ActivateAsync(_ct);

        Assert.True(report!.Passed);
        Assert.Equal(AgentState.Active, _runtime.State);
        var connection = Assert.Single(_connections);
        Assert.True(connection.Started);
        var status = _runtime.Status();
        Assert.Equal(("agt_01", true, true), (status.AgentId, status.ServerOnline, status.IsExecutor));
        Assert.Contains("Server: https://race.example/, paired as agt_01, reachable, commands by long-poll", status.Describe());

        await _runtime.SleepAsync();

        Assert.Equal(AgentState.Sleeping, _runtime.State);
        Assert.Equal(AgentState.Sleeping, _server.SentOf<HeartbeatMessage>().Last().State);
        Assert.True(connection.Disposed);
        Assert.Null(_runtime.Status().ServerOnline);
        Assert.Same(report, _runtime.Status().LastSelfTest);
    }

    [Fact]
    public async Task Activate_applies_the_typed_without_focus_setting()
    {
        Pair();
        Assert.Equal(TypedChatWay.Posted, _typed.DefaultWay);
        _config.Save(new AgentConfig { TypedWithoutFocus = false });

        await _runtime.ActivateAsync(_ct);

        Assert.Equal(TypedChatWay.Focus, _typed.DefaultWay);
    }

    [Fact]
    public async Task A_failed_self_test_goes_back_to_sleep_closes_the_connection_and_keeps_the_report()
    {
        Pair();
        _sim.IsConnected = false;
        _sim.Session = null;

        var report = await _runtime.ActivateAsync(_ct);

        Assert.False(report!.Passed);
        Assert.Equal(AgentState.Sleeping, _runtime.State);
        Assert.True(Assert.Single(_connections).Disposed);
        var status = _runtime.Status();
        Assert.Same(report, status.LastSelfTest);
        Assert.NotNull(status.Problem);
        Assert.Contains(AgentStatus.DescribeSteps(report), l => l.StartsWith("FAIL 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_out_of_reach_leaves_it_sleeping_with_the_reason()
    {
        Pair();
        var runtime = new AgentRuntime(new RuntimeParts
        {
            Sim = _sim,
            Typed = new TypedChatSender(new FakeDesktop(_clock), new FakeDesktop(_clock), new FakeDesktop(_clock), _clock),
            MacroConfig = _ => new NoMacroConfig(),
            Prompt = new FakePrompt(),
            Connect = _ => new FakeConnection(_server) { FailStart = new HttpRequestException("No such host is known.") },
            LocalFailsafeStore = new MemoryLocalFailsafeStore(),
            Clock = _clock,
            RunLoop = false,
        }, _config, _credentials, "0.1.0-test");

        Assert.Null(await runtime.ActivateAsync(_ct));

        Assert.Equal(AgentState.Sleeping, runtime.State);
        Assert.Equal("Cannot reach https://race.example/: No such host is known.", runtime.Problem);
    }

    [Fact]
    public async Task Settings_errors_or_credentials_for_another_server_stop_it_before_the_self_test()
    {
        Pair(new Uri("https://other.example/"));
        _config.Save(new AgentConfig { Server = Server });

        Assert.Null(await _runtime.ActivateAsync(_ct));
        Assert.Equal("Paired with https://other.example/, not https://race.example/. Pair again.", _runtime.Problem);

        File.WriteAllText(_config.Path, """{"league": "x"}""");
        Assert.Null(await _runtime.ActivateAsync(_ct));
        Assert.StartsWith("The settings file has errors: ", _runtime.Problem, StringComparison.Ordinal);
        Assert.Empty(_connections);
        Assert.NotEmpty(_runtime.Status().SettingsErrors);
    }

    [Fact]
    public async Task The_settings_file_decides_the_macros_league_and_local_failsafe()
    {
        Pair();
        _config.Save(new AgentConfig
        {
            League = 4242,
            Macros = new MacroSettings { Enabled = true, Slots = new Dictionary<int, string> { [14] = "!pitclose" } },
            LocalFailsafe = new LocalFailsafeSettings { Enabled = true, DelaySeconds = 45 },
        });

        await _runtime.ActivateAsync(_ct);

        var status = _runtime.Status();
        Assert.Equal((true, 45), (status.LocalFailsafe.Enabled, status.LocalFailsafe.DelaySeconds));
        Assert.Contains("Local failsafe: on (after 45 s)", status.Describe());
        Assert.NotEqual(ChannelReadiness.Disabled, status.Channels!.Macro);
        await _runtime.SleepAsync();
    }
}
