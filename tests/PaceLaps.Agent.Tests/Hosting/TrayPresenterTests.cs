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
        Assert.Equal(new TrayView(TrayIcon.Sleeping, "PaceLaps Race Control: Sleeping, Activate to run the self-test", true, false, true), TrayPresenter.Present(Sleeping()));
        Assert.Equal(TrayIcon.Busy, TrayPresenter.Present(Sleeping() with { Busy = true }).Icon);
        Assert.False(TrayPresenter.Present(Sleeping() with { Busy = true }).CanActivate);
        Assert.Equal(new TrayView(TrayIcon.Active, "PaceLaps Race Control: Active, executor, Race 1", false, true, false), TrayPresenter.Present(Active()));
        Assert.Equal("PaceLaps Race Control: Active, observer, Race 1", TrayPresenter.Present(Active(executor: false)).Tooltip);
        Assert.Equal("PaceLaps Race Control: Active, view only (replays), Race 1",
            TrayPresenter.Present(Active(executor: false) with { Channels = Ready with { Typed = ChannelReadiness.Disabled } }).Tooltip);
        Assert.Equal("PaceLaps Race Control: Ready, The sim is not in a session.",
            TrayPresenter.Present(Active() with { State = AgentState.Ready, SessionName = null, NotActiveReason = "The sim is not in a session." }).Tooltip);
        Assert.Equal("PaceLaps Race Control: Sleeping, not paired yet", TrayPresenter.Present(Sleeping() with { AgentId = null }).Tooltip);
    }

    [Fact]
    public void Problems_turn_it_red_and_say_what_is_wrong()
    {
        Assert.Equal((TrayIcon.Problem, "PaceLaps Race Control: Sleeping, This PC is not paired with a server yet. Pair it first."),
            (TrayPresenter.Present(Sleeping("This PC is not paired with a server yet. Pair it first.")).Icon,
             TrayPresenter.Present(Sleeping("This PC is not paired with a server yet. Pair it first.")).Tooltip));
        Assert.Equal(TrayIcon.Problem, TrayPresenter.Present(Active() with { ServerOnline = false }).Icon);
        Assert.Equal(TrayIcon.Problem, TrayPresenter.Present(Active() with { Channels = Ready with { Typed = ChannelReadiness.Failed } }).Icon);
        Assert.EndsWith("LOCAL FAILSAFE RUNNING", TrayPresenter.Present(Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch }).Tooltip, StringComparison.Ordinal);
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
        Assert.Equal([new TrayNotice("Self-test failed", "Step 2 Session: not in a session", true)],
            TrayPresenter.Notices(Sleeping(), Sleeping("self-test failed") with { LastSelfTest = failed }));
        Assert.Empty(TrayPresenter.Notices(Sleeping("self-test failed") with { LastSelfTest = failed }, Sleeping("self-test failed") with { LastSelfTest = failed }));
        Assert.Equal("Not activated", Assert.Single(TrayPresenter.Notices(Sleeping(), Sleeping("This PC is not paired"))).Title);
    }

    [Fact]
    public void Executor_server_typing_and_failsafe_changes_are_told()
    {
        Assert.Equal("Executor", Assert.Single(TrayPresenter.Notices(Active(executor: false), Active())).Title);
        Assert.Equal("No longer the executor", Assert.Single(TrayPresenter.Notices(Active(), Active(executor: false))).Title);
        Assert.Empty(TrayPresenter.Notices(Active(), Sleeping()));
        Assert.Equal("Server unreachable", Assert.Single(TrayPresenter.Notices(Active(), Active() with { ServerOnline = false })).Title);
        Assert.Equal("Server reachable again", Assert.Single(TrayPresenter.Notices(Active() with { ServerOnline = false }, Active())).Title);
        Assert.Equal("Typing into iRacing failed",
            Assert.Single(TrayPresenter.Notices(Active(), Active() with { Channels = Ready with { Typed = ChannelReadiness.Failed } })).Title);
        var failsafe = Assert.Single(TrayPresenter.Notices(Active(), Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch, LocalFailsafeHeld = "local-failsafe-default v2" }));
        Assert.Equal(("Local failsafe started", true), (failsafe.Title, failsafe.Warning));
        Assert.Contains("local-failsafe-default v2", failsafe.Text, StringComparison.Ordinal);
    }
}
