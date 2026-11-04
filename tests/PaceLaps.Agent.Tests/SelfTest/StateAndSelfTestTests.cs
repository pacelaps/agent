using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.States;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.SelfTest;

public sealed class AgentStateMachineTests
{
    [Fact]
    public void Normal_path_sleeping_selftest_ready_active()
    {
        var m = new AgentStateMachine();
        var changes = new List<(AgentState, AgentState)>();
        m.Changed += (from, to, _) => changes.Add((from, to));

        Assert.True(m.Activate());
        Assert.True(m.SelfTestPassed());
        Assert.True(m.Joined());

        Assert.Equal(AgentState.Active, m.State);
        Assert.Equal([(AgentState.Sleeping, AgentState.SelfTest), (AgentState.SelfTest, AgentState.Ready), (AgentState.Ready, AgentState.Active)], changes);
    }

    [Fact]
    public void A_failed_self_test_returns_to_sleeping_with_the_reason()
    {
        var m = new AgentStateMachine();
        m.Activate();

        Assert.True(m.SelfTestFailed("step 5 (typed channel): focus failed"));
        Assert.Equal(AgentState.Sleeping, m.State);
        Assert.Equal("step 5 (typed channel): focus failed", m.LastFailure);

        m.Activate();
        m.SelfTestPassed();
        Assert.Null(m.LastFailure);
    }

    [Fact]
    public void Transitions_out_of_order_are_refused()
    {
        var m = new AgentStateMachine();

        Assert.False(m.SelfTestPassed());
        Assert.False(m.Joined());
        Assert.False(m.SelfTestFailed("x"));
        Assert.False(m.Sleep());
        m.Activate();
        Assert.False(m.Activate());
        Assert.False(m.Joined());
        Assert.Equal(AgentState.SelfTest, m.State);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_operator_can_go_to_sleep_from_any_state(int steps)
    {
        var m = new AgentStateMachine();
        m.Activate();
        if (steps > 1)
        {
            m.SelfTestPassed();
        }

        if (steps > 2)
        {
            m.Joined();
        }

        Assert.True(m.Sleep());
        Assert.Equal(AgentState.Sleeping, m.State);
    }

    [Fact]
    public void Leaving_a_session_returns_to_ready()
    {
        var m = new AgentStateMachine();
        m.Activate();
        m.SelfTestPassed();
        m.Joined();

        Assert.True(m.Left("session ended"));
        Assert.Equal(AgentState.Ready, m.State);
    }
}

public sealed class ClockOffsetEstimatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Uses_the_sample_with_the_shortest_round_trip()
    {
        var e = new ClockOffsetEstimator();
        Assert.False(e.HasEstimate);
        Assert.Equal(TimeSpan.Zero, e.Offset);

        // Server 100 ms ahead. A slow sample (300 ms, asymmetric) misleads; the fast one (20 ms) is accurate.
        e.Add(T0, T0.AddMilliseconds(100 + 250), T0.AddMilliseconds(300));
        e.Add(T0.AddSeconds(1), T0.AddSeconds(1).AddMilliseconds(100 + 10), T0.AddSeconds(1).AddMilliseconds(20));

        Assert.Equal(TimeSpan.FromMilliseconds(100), e.Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(20), e.RoundTrip);
    }

    [Fact]
    public void Old_samples_leave_the_window_and_negative_round_trips_are_ignored()
    {
        var e = new ClockOffsetEstimator(window: 2);
        e.Add(T0, T0.AddMilliseconds(5), T0.AddMilliseconds(10));
        e.Add(T0, T0.AddMilliseconds(500), T0.AddMilliseconds(100));
        e.Add(T0, T0.AddMilliseconds(600), T0.AddMilliseconds(100));
        e.Add(T0, T0, T0.AddMilliseconds(-5));

        Assert.Equal(TimeSpan.FromMilliseconds(450), e.Offset);
    }
}

public sealed class SelfTestRunnerTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;
    private readonly FakeSim _sim = new();
    private readonly FakeServer _server;
    private readonly ClockOffsetEstimator _offset = new();

    public SelfTestRunnerTests()
    {
        _desktop = new FakeDesktop(_clock);
        _server = new FakeServer(_clock);
    }

    private SelfTestRunner Runner(FakePrompt prompt, SelfTestOptions? options = null, MacroTable? macros = null) =>
        new(_sim, _server, new TypedChatSender(_desktop, _desktop, _desktop, _clock), macros ?? MacroTable.Empty, prompt, _offset, _clock, options);

    private Task<SelfTestReport> Run(FakePrompt prompt, SelfTestOptions? options = null, MacroTable? macros = null) =>
        Runner(prompt, options, macros).RunAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Passes_all_steps_and_offers_only_verified_channels()
    {
        var report = await Run(new FakePrompt(true, true));

        Assert.True(report.Passed, report.FirstFailure);
        Assert.Equal([1, 2, 3, 4, 5, 6], report.Steps.Select(s => s.Number));
        Assert.Equal(new ChannelSet(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Ready), report.Channels);
        Assert.Equal("!help", _desktop.Typed);
        Assert.Equal(TimeSpan.FromMilliseconds(120), _offset.Offset);
        Assert.Equal(new SessionIdentity(12345, 6789, 111, 222, 2, "Race"), report.Identity);
    }

    [Fact]
    public async Task Fails_at_step_1_without_the_sim()
    {
        _sim.IsConnected = false;

        var report = await Run(new FakePrompt());

        Assert.False(report.Passed);
        Assert.StartsWith("step 1 (sim and telemetry)", report.FirstFailure, StringComparison.Ordinal);
        Assert.Empty(_server.Checked);
    }

    [Fact]
    public async Task A_session_without_league_id_fails_unless_the_operator_selected_the_league()
    {
        _sim.Session = SimFixtures.Session(leagueId: 0);

        var failed = await Run(new FakePrompt());
        Assert.Contains("not a league session", failed.FirstFailure, StringComparison.Ordinal);

        var passed = await Run(new FakePrompt(true, true), new SelfTestOptions { LeagueOverride = 999 });
        Assert.True(passed.Passed, passed.FirstFailure);
        Assert.Equal(999, _server.Checked.Last().LeagueId);
        Assert.Contains("selected by the operator", passed.Steps[1].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hosted_AI_session_without_iRacing_ids_passes_step_2()
    {
        // The acceptance run failed here: a hosted AI session has session id and sub-session id 0.
        _sim.Session = SimFixtures.Session(leagueId: 0) with { SessionId = 0, SubSessionId = 0 };

        var report = await Run(new FakePrompt(true, true), new SelfTestOptions { LeagueOverride = 1 });

        Assert.True(report.Passed, report.FirstFailure);
        Assert.Contains("no iRacing session ids (hosted AI session)", report.Steps[1].Detail, StringComparison.Ordinal);
        Assert.Equal(new SessionIdentity(1, 6789, 0, 0, 2, "Race"), report.Identity);
    }

    [Fact]
    public async Task Fails_at_step_2_without_a_session()
    {
        _sim.Session = SimFixtures.Session() with { Cars = [], SessionType = "Unknown" };

        var report = await Run(new FakePrompt());

        Assert.Equal("step 2 (session identity): not in a session", report.FirstFailure);
    }

    [Fact]
    public async Task Fails_at_step_2_when_the_operator_is_not_a_steward()
    {
        _server.Check = new SessionCheck(true, false);

        var report = await Run(new FakePrompt());

        Assert.Contains("not a steward", report.FirstFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_at_step_3_when_the_server_does_not_answer()
    {
        _server.AnswerPings = false;

        var report = await Run(new FakePrompt());

        Assert.StartsWith("step 3", report.FirstFailure, StringComparison.Ordinal);
        Assert.Equal("", _desktop.Typed);
    }

    [Fact]
    public async Task Fails_at_step_5_when_the_key_binding_rule_is_not_confirmed_without_typing()
    {
        var report = await Run(new FakePrompt(false));

        Assert.Contains("plain key", report.FirstFailure, StringComparison.Ordinal);
        Assert.Equal(ChannelReadiness.Failed, report.Channels.Typed);
        Assert.Equal("", _desktop.Typed);
    }

    [Fact]
    public async Task Fails_at_step_5_when_typing_fails_or_no_reply_is_seen()
    {
        _desktop.SimRefusesFocus = true;
        var focus = await Run(new FakePrompt(true));
        Assert.Contains("FocusFailed", focus.FirstFailure, StringComparison.Ordinal);

        _desktop.SimRefusesFocus = false;
        var noReply = await Run(new FakePrompt(true, false));
        Assert.Contains("no reply to !help", noReply.FirstFailure, StringComparison.Ordinal);
        Assert.Equal(ChannelReadiness.Failed, noReply.Channels.Typed);
    }

    [Fact]
    public async Task The_macro_channel_is_ready_only_when_confirmed_and_never_fails_the_test()
    {
        var macros = new MacroTable([(14, "!pitclose$")]);
        var options = new SelfTestOptions { MacroEnabled = true };

        var confirmed = await Run(new FakePrompt(true, true, true), options, macros);
        Assert.True(confirmed.Passed);
        Assert.Equal(ChannelReadiness.Ready, confirmed.Channels.Macro);

        var refused = await Run(new FakePrompt(true, true, false), options, macros);
        Assert.True(refused.Passed);
        Assert.Equal(ChannelReadiness.Unverified, refused.Channels.Macro);
    }

    [Fact]
    public async Task The_replay_channel_is_ready_unless_switched_off()
    {
        var off = await Run(new FakePrompt(true, true), new SelfTestOptions { ReplayEnabled = false });

        Assert.Equal(ChannelReadiness.Disabled, off.Channels.Sdk);
    }

    [Fact]
    public async Task An_operator_without_admin_rights_passes_view_only_without_typing()
    {
        _sim.Session = SimFixtures.Session() with { DriverIsAdmin = false };

        var report = await Run(new FakePrompt());

        Assert.True(report.Passed, report.FirstFailure);
        Assert.Equal(new ChannelSet(ChannelReadiness.Disabled, ChannelReadiness.Disabled, ChannelReadiness.Ready), report.Channels);
        Assert.Equal(SelfTestStepResult.Skipped, report.Steps.Last().Outcome);
        Assert.Equal("view only: not an admin in this session", report.Steps.Last().Shared);
        Assert.Equal("", _desktop.Typed);
    }

    [Fact]
    public async Task Without_the_replay_channel_an_operator_without_admin_rights_fails()
    {
        _sim.Session = SimFixtures.Session() with { DriverIsAdmin = false };

        var report = await Run(new FakePrompt(), new SelfTestOptions { ReplayEnabled = false });

        Assert.False(report.Passed);
        Assert.Contains("not an admin", report.FirstFailure, StringComparison.Ordinal);
        Assert.Equal(ChannelReadiness.Failed, report.Channels.Typed);
        Assert.Equal("", _desktop.Typed);
    }
}
