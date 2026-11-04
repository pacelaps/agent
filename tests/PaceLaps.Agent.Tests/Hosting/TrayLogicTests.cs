using System.Net;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;
using PaceLaps.Agent.Tests.Server;
using Microsoft.Win32;
using Serilog.Events;
using Serilog.Parsing;

namespace PaceLaps.Agent.Tests.Hosting;

public sealed class PairingFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "irc-pairing-" + Guid.NewGuid().ToString("N"));
    private readonly CredentialStore _credentials;
    private readonly ConfigStore _config;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public PairingFlowTests()
    {
        _credentials = new CredentialStore(Path.Combine(_dir, "credentials.bin"));
        _config = new ConfigStore(Path.Combine(_dir, "config.json"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private PairingFlow Flow(FakeApi api) => new(_credentials, _config, "0.1.0", new FakeClock(), url => new PairingClient(url, new FakeClock(), api));

    private static FakeApi Approving() => new()
    {
        Answer = (path, _) => path == "/api/v1/agent/pairing"
            ? (HttpStatusCode.OK, ContractSchemas.Example("pairing_code.json"))
            : (HttpStatusCode.OK, ContractSchemas.Example("pairing_token.json")),
    };

    [Fact]
    public async Task Pairing_shows_the_code_stores_the_credentials_and_makes_it_the_settings_server()
    {
        _config.Save(new AgentConfig { Server = new Uri("https://old.example/"), League = 7 });
        PairingCodeMessage? shown = null;

        var outcome = await Flow(Approving()).PairAsync(" https://race.example ", c => shown = c, _ct);

        Assert.Equal(new PairingOutcome(true, "Paired as agt_7f3k9q2m4x8w with https://race.example/."), outcome);
        Assert.Equal("KXQ7-M3RT", shown!.UserCode);
        Assert.Equal(("agt_7f3k9q2m4x8w", new Uri("https://race.example/")), (_credentials.Load()!.AgentId, _credentials.Load()!.Server));
        Assert.Equal((new Uri("https://race.example/"), 7), (_config.Load().Server, _config.Load().League));
    }

    [Fact]
    public async Task A_bad_address_a_denial_or_an_unreachable_server_is_said_plainly()
    {
        var api = Approving();

        Assert.False((await Flow(api).PairAsync("http://race.example", _ => { }, _ct)).Paired);
        Assert.Empty(api.Requests);

        var denied = new FakeApi
        {
            Answer = (path, _) => path == "/api/v1/agent/pairing"
                ? (HttpStatusCode.OK, ContractSchemas.Example("pairing_code.json"))
                : (HttpStatusCode.OK, """{"v":1,"kind":"pairing_status","status":"denied"}"""),
        };
        Assert.Equal("Pairing failed: The pairing was denied on the web page.", (await Flow(denied).PairAsync("https://race.example", _ => { }, _ct)).Message);
        Assert.StartsWith("Pairing failed: ", (await Flow(new FakeApi { Down = true }).PairAsync("https://race.example", _ => { }, _ct)).Message, StringComparison.Ordinal);
        Assert.Null(_credentials.Load());
    }

    [Fact]
    public async Task A_release_build_pairs_only_with_its_own_server()
    {
        var fixedServer = new Uri("https://racecontrol.example/");
        var flow = new PairingFlow(_credentials, _config, "0.1.0", new FakeClock(), url => new PairingClient(url, new FakeClock(), Approving()), fixedServer);

        var outcome = await flow.PairAsync("https://elsewhere.example", _ => { }, _ct);

        Assert.Equal(new PairingOutcome(true, "Paired as agt_7f3k9q2m4x8w with https://racecontrol.example/."), outcome);
        Assert.Equal(fixedServer, _credentials.Load()!.Server);
        Assert.Equal("https://racecontrol.example/", flow.SuggestedServer());
        Assert.Null(ReleaseBuild.Server(typeof(PairingFlowTests).Assembly));
    }

    [Fact]
    public async Task Cancelling_stops_waiting_and_keeps_the_old_credentials()
    {
        _credentials.Save(new AgentCredentials(new Uri("https://race.example/"), "agt_old", "t"));
        var pending = new FakeApi
        {
            Answer = (path, _) => path == "/api/v1/agent/pairing"
                ? (HttpStatusCode.OK, ContractSchemas.Example("pairing_code.json"))
                : (HttpStatusCode.OK, ContractSchemas.Example("pairing_status.json")),
        };
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_ct);

        var outcome = await Flow(pending).PairAsync("https://race.example", _ => cancel.Cancel(), cancel.Token);

        Assert.Equal(new PairingOutcome(false, "Pairing cancelled."), outcome);
        Assert.Equal("agt_old", _credentials.Load()!.AgentId);
        Assert.Equal("https://race.example/", Flow(pending).SuggestedServer());
    }
}

public sealed class SettingsModelTests
{
    [Fact]
    public void The_fields_round_trip_through_the_settings_file_format()
    {
        var config = new AgentConfig
        {
            Server = new Uri("https://race.example/"),
            League = 15457,
            Macros = new MacroSettings { Enabled = true, Slots = new Dictionary<int, string> { [14] = "!pitclose", [15] = "!pitopen" } },
            LocalFailsafe = new LocalFailsafeSettings { Enabled = true, DelaySeconds = 45 },
            AppIni = @"D:\iRacing\app.ini",
            TypedWithoutFocus = false,
        };
        var model = SettingsModel.From(config, startWithWindows: true);

        Assert.Equal(("!pitclose", "", true, false), (model.MacroSlots[13], model.MacroSlots[0], model.StartWithWindows, model.TypedWithoutFocus));
        var (back, errors) = model.ToConfig();
        Assert.Empty(errors);
        Assert.Equal(config.ToJson(), back!.ToJson());
    }

    [Fact]
    public void Empty_fields_are_the_defaults()
    {
        var (config, errors) = new SettingsModel().ToConfig();

        Assert.Empty(errors);
        Assert.Equal(new AgentConfig().ToJson(), config!.ToJson());
    }

    [Fact]
    public void Every_bad_field_is_named()
    {
        var model = new SettingsModel { Server = "ftp://x", League = "twelve", LocalFailsafeDelay = "5" };
        model.MacroSlots[2] = new string('x', MacroSettings.MaxTextLength + 1);

        var (config, errors) = model.ToConfig();

        Assert.Null(config);
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("server", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("league", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("localFailsafe.delaySeconds", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains('3', StringComparison.Ordinal));
    }
}

public sealed class TrayPlumbingTests
{
    [Fact]
    public void Start_with_windows_writes_and_removes_the_run_value()
    {
        var key = $@"Software\PaceLaps\Tests\Run-{Guid.NewGuid():N}";
        try
        {
            var autoStart = new RunKeyAutoStart("\"C:\\Program Files\\PaceLaps\\pacelaps-agent.exe\"", keyPath: key);
            Assert.False(autoStart.Enabled);

            autoStart.Set(true);
            Assert.True(autoStart.Enabled);
            Assert.False(new RunKeyAutoStart("\"C:\\elsewhere\\pacelaps-agent.exe\"", keyPath: key).Enabled);

            autoStart.Set(false);
            Assert.False(autoStart.Enabled);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task A_second_start_finds_the_first_and_asks_it_to_show_itself()
    {
        var name = $"PaceLaps.Agent.Test.{Guid.NewGuid():N}";
        using var first = SingleInstance.TryAcquire(name);
        var shown = new TaskCompletionSource();
        first!.ShowRequested += () => shown.TrySetResult();

        Assert.Null(SingleInstance.TryAcquire(name));

        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void The_recent_log_keeps_the_last_lines_and_counts_them()
    {
        var log = new RecentLog(capacity: 2);
        foreach (var text in new[] { "one", "two", "three" })
        {
            log.Emit(new LogEvent(DateTimeOffset.Now, LogEventLevel.Warning, null, new MessageTemplateParser().Parse(text), []));
        }

        Assert.Equal(3, log.Version);
        Assert.Equal(["WARN  two", "WARN  three"], log.Lines().Select(l => l[9..]));
    }
}
