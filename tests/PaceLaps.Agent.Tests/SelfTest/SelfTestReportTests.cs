using PaceLaps.Agent.Core.Agent;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.SelfTest;

/// <summary>What the self-test reports to the server (self_test_result): outcomes, and details without names.</summary>
public sealed class SelfTestReportTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 7, 18, 55, 2, 120, TimeSpan.Zero);
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;
    private readonly FakeSim _sim = new();
    private readonly FakeServer _server;

    public SelfTestReportTests()
    {
        _desktop = new FakeDesktop(_clock);
        _server = new FakeServer(_clock);
    }

    /// <summary>The sim's app.ini under a Windows user folder that carries the operator's name.</summary>
    private sealed class NamedPathConfig(IReadOnlyDictionary<int, string> slots) : IMacroConfigSource
    {
        public IReadOnlyDictionary<int, string>? ReadSlots() => slots;

        public string Location => @"C:\Users\Jane Doe\Documents\iRacing\app.ini";
    }

    private Task<SelfTestReport> Run(FakePrompt prompt, bool macros = false, IMacroConfigSource? config = null) =>
        new SelfTestRunner(_sim, _server, new TypedChatSender(_desktop, _desktop, _desktop, _clock), new MacroTable([(14, "!pitclose$")]),
            prompt, new ClockOffsetEstimator(), _clock, new SelfTestOptions { MacroEnabled = macros }, config).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_passed_self_test_reports_every_step_with_the_macro_channel_skipped()
    {
        var report = await Run(new FakePrompt(true, true));
        var message = report.ToContract("agt_01", T);

        Assert.True(report.ReachedServer);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(message));
        Assert.True(message.Passed);
        Assert.Equal(
            [SelfTestStepResult.Pass, SelfTestStepResult.Pass, SelfTestStepResult.Pass, SelfTestStepResult.Pass, SelfTestStepResult.Pass, SelfTestStepResult.Skipped],
            message.Steps.Select(s => s.Result));
        Assert.Equal(SelfTestStepName.MacroChannel, message.Steps[5].Name);
        Assert.Equal("macro channel off", message.Steps[5].Detail);
        Assert.All(message.Steps.Take(5), s => Assert.Null(s.Detail));
    }

    [Fact]
    public async Task A_failed_step_is_reported_with_a_short_reason()
    {
        _sim.Session = SimFixtures.Session() with { Cars = [], SessionType = "Unknown" };

        var message = (await Run(new FakePrompt())).ToContract("agt_01", T);

        Assert.False(message.Passed);
        var failed = message.Steps.Last();
        Assert.Equal((2, SelfTestStepName.Session, SelfTestStepResult.Fail), (failed.Step, failed.Name, failed.Result));
        Assert.Equal("not in a session", failed.Detail);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(message));
    }

    [Fact]
    public async Task A_view_only_agent_reports_the_typed_channel_skipped()
    {
        _sim.Session = SimFixtures.Session() with { DriverIsAdmin = false };

        var message = (await Run(new FakePrompt())).ToContract("agt_01", T);

        Assert.True(message.Passed);
        var typed = message.Steps.Last();
        Assert.Equal((5, SelfTestStepName.TypedChannel, SelfTestStepResult.Skipped), (typed.Step, typed.Name, typed.Result));
        Assert.Equal("view only: not an admin in this session", typed.Detail);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(message));
    }

    [Fact]
    public async Task No_name_or_path_reaches_the_server()
    {
        var report = await Run(new FakePrompt(true, true), macros: true, new NamedPathConfig(new Dictionary<int, string> { [14] = "!pitclose$" }));
        var mismatch = await Run(new FakePrompt(true, true), macros: true, new NamedPathConfig(new Dictionary<int, string> { [14] = "my own macro" }));

        // The operator's console shows where the file is; the server never sees it.
        Assert.Contains("Jane Doe", report.Steps[5].Detail, StringComparison.Ordinal);
        foreach (var json in new[] { report, mismatch }.Select(r => ContractJson.Serialize(r.ToContract("agt_01", T))))
        {
            Assert.DoesNotContain("Jane", json, StringComparison.Ordinal);
            Assert.DoesNotContain(@"\\", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Example Operator", json, StringComparison.Ordinal);
        }

        Assert.Equal((SelfTestStepResult.Unverified, "1 slot(s) do not match the sim's saved macros"),
            (mismatch.ToContract("agt_01", T).Steps[5].Result, mismatch.ToContract("agt_01", T).Steps[5].Detail));
    }

    [Fact]
    public async Task A_self_test_that_never_reached_the_server_reports_nothing()
    {
        _sim.IsConnected = false;
        var host = new AgentHost(new AgentHostOptions { AgentId = "agt_01", AgentVersion = "0.1.0-test" }, _sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, null, new FakePrompt(), _clock);

        var report = await host.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.False(report!.ReachedServer);
        Assert.Empty(_server.Sent);
    }

    [Fact]
    public async Task The_host_sends_the_result_of_a_self_test_the_server_answered()
    {
        var host = new AgentHost(new AgentHostOptions { AgentId = "agt_01", AgentVersion = "0.1.0-test" }, _sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, null, new FakePrompt(true, true), _clock);

        await host.ActivateAsync(TestContext.Current.CancellationToken);

        var sent = Assert.Single(_server.SentOf<SelfTestResultMessage>());
        Assert.True(sent.Passed);
        Assert.Equal("agt_01", sent.AgentId);
    }
}
