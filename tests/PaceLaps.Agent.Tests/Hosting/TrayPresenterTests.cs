using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tests.Hosting;

public sealed class TrayPresenterTests
{
    private static readonly ChannelSet Ready = new(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Disabled);

    private static AgentStatus Sleeping(string? problem = null) => new() { State = AgentState.Sleeping, AgentId = "agt_01", Problem = problem };

    private static AgentStatus Active(bool executor = true) => new()
    {
        State = AgentState.Active, AgentId = "agt_01", ServerOnline = true, IsExecutor = executor, SessionName = "Race 1", Channels = Ready,
    };

    private static SelfTestReport Report(bool passed) => new(passed,
        [new SelfTestStep(1, "Sim", true, "iRacing runs"), new SelfTestStep(2, "Session", passed, passed ? "league 12" : "not in a session")], Ready, null);

    [Fact]
    public void Each_state_has_its_icon_tooltip_and_menu()
    {
        Assert.Equal(new TrayView(TrayIcon.Sleeping, "PaceLaps Agent: Sleeping, Activate in your session", true, false, true), TrayPresenter.Present(Sleeping()));
        Assert.Equal(TrayIcon.Busy, TrayPresenter.Present(Sleeping() with { Busy = true }).Icon);
        Assert.False(TrayPresenter.Present(Sleeping() with { Busy = true }).CanActivate);
        Assert.Equal(new TrayView(TrayIcon.Active, "PaceLaps Agent: In a session, sends the commands (Race 1)", false, true, false), TrayPresenter.Present(Active()));
        Assert.Equal("PaceLaps Agent: In a session, standby (Race 1)", TrayPresenter.Present(Active(executor: false)).Tooltip);
        Assert.Equal("PaceLaps Agent: In a session, view only (replays) (Race 1)",
            TrayPresenter.Present(Active(executor: false) with { Channels = Ready with { Typed = ChannelReadiness.Disabled } }).Tooltip);
        Assert.Equal("PaceLaps Agent: Ready, The sim is not in a session.",
            TrayPresenter.Present(Active() with { State = AgentState.Ready, SessionName = null, NotActiveReason = "The sim is not in a session." }).Tooltip);
        Assert.Equal("PaceLaps Agent: Sleeping, not paired yet", TrayPresenter.Present(Sleeping() with { AgentId = null }).Tooltip);
    }

    [Fact]
    public void Problems_turn_it_red_and_say_what_is_wrong()
    {
        Assert.Equal((TrayIcon.Problem, "PaceLaps Agent: Sleeping, This PC is not paired with a server yet. Pair it first."),
            (TrayPresenter.Present(Sleeping("This PC is not paired with a server yet. Pair it first.")).Icon,
             TrayPresenter.Present(Sleeping("This PC is not paired with a server yet. Pair it first.")).Tooltip));
        Assert.Equal(TrayIcon.Problem, TrayPresenter.Present(Active() with { ServerOnline = false }).Icon);
        Assert.Equal(TrayIcon.Problem, TrayPresenter.Present(Active() with { Channels = Ready with { Typed = ChannelReadiness.Failed } }).Icon);
        Assert.EndsWith("local failsafe running", TrayPresenter.Present(Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch }).Tooltip, StringComparison.Ordinal);
        Assert.Equal(TrayIcon.Problem, TrayPresenter.Present(Sleeping() with { SettingsErrors = ["unknown setting 'x'"] }).Icon);
    }

    [Fact]
    public void A_long_tooltip_is_cut_to_what_windows_shows()
    {
        var tooltip = TrayPresenter.Present(Sleeping(new string('x', 300))).Tooltip;

        Assert.Equal(TrayPresenter.MaxTooltip, tooltip.Length);
        Assert.EndsWith("...", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_told_at_start_then_each_change_once()
    {
        Assert.Empty(TrayPresenter.Notices(null, Active()));
        Assert.Empty(TrayPresenter.Notices(Active(), Active()));

        var failed = Report(passed: false);
        Assert.Equal([new TrayNotice("Self-test failed", "Session: not in a session", true)],
            TrayPresenter.Notices(Sleeping(), Sleeping("self-test failed") with { LastSelfTest = failed }));
        Assert.Empty(TrayPresenter.Notices(Sleeping("self-test failed") with { LastSelfTest = failed }, Sleeping("self-test failed") with { LastSelfTest = failed }));
        Assert.Equal("Not activated", Assert.Single(TrayPresenter.Notices(Sleeping(), Sleeping("This PC is not paired"))).Title);
    }

    [Fact]
    public void Executor_server_typing_and_failsafe_changes_are_told()
    {
        Assert.Equal("You send the commands", Assert.Single(TrayPresenter.Notices(Active(executor: false), Active())).Title);
        Assert.Equal("Standby", Assert.Single(TrayPresenter.Notices(Active(), Active(executor: false))).Title);
        Assert.Empty(TrayPresenter.Notices(Active(), Sleeping()));
        Assert.Equal("Can't reach Race Control", Assert.Single(TrayPresenter.Notices(Active(), Active() with { ServerOnline = false })).Title);
        Assert.Equal("Race Control is back", Assert.Single(TrayPresenter.Notices(Active() with { ServerOnline = false }, Active())).Title);
        Assert.Equal("Typing into iRacing failed",
            Assert.Single(TrayPresenter.Notices(Active(), Active() with { Channels = Ready with { Typed = ChannelReadiness.Failed } })).Title);
        var failsafe = Assert.Single(TrayPresenter.Notices(Active(), Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch, LocalFailsafeHeld = "local-failsafe-default v2" }));
        Assert.Equal(("Local failsafe started", true), (failsafe.Title, failsafe.Warning));
        Assert.Contains("local-failsafe-default v2", failsafe.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Operator_words_name_the_states_roles_and_steps_without_internal_terms()
    {
        Assert.Equal(["Sleeping", "Self-test running", "Ready", "In a session"],
            new[] { AgentState.Sleeping, AgentState.SelfTest, AgentState.Ready, AgentState.Active }.Select(x => OperatorWords.State(Sleeping() with { State = x })));
        Assert.Equal(["iRacing", "Session", "Race Control", "Cars", "Typing into iRacing", "Chat macros"], Enumerable.Range(1, 6).Select(OperatorWords.StepTitle));
        Assert.Equal("Typing into iRacing: no reply", OperatorWords.Step(new SelfTestStep(5, "typed channel", false, "no reply")));

        string[] internals = ["epoch", "observer", "channel", "long-poll", "websocket", "executor"];
        var texts = new[] { Sleeping(), Sleeping() with { AgentId = null }, Active(), Active(executor: false), Active() with { ServerOnline = false },
                Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch }, Active() with { Channels = Ready with { Typed = ChannelReadiness.Failed } } }
            .Select(s => TrayPresenter.Present(s).Tooltip)
            .Concat(TrayPresenter.Notices(Active(executor: false), Active() with { ServerOnline = false, Channels = Ready with { Typed = ChannelReadiness.Failed } }).Select(n => n.Title + " " + n.Text))
            .Concat(TrayPresenter.Notices(Active(), Active(executor: false)).Select(n => n.Title + " " + n.Text));
        foreach (var text in texts)
        {
            Assert.DoesNotContain(internals, word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }
}
