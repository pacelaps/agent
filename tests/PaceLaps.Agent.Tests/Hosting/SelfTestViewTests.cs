using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tests.Hosting;

public sealed class SelfTestViewTests
{
    private static readonly ChannelSet Channels = new(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Ready);

    private static AgentStatus Sleeping() => new() { State = AgentState.Sleeping, AgentId = "agt_01" };

    private static SelfTestStep Ok(int n) => new(n, "step", true, $"ok {n}");

    [Fact]
    public void While_it_runs_the_finished_steps_show_and_the_next_one_runs()
    {
        var view = SelfTestView.From(Sleeping() with { State = AgentState.SelfTest, SelfTestProgress = [Ok(1), Ok(2), Ok(3), Ok(4)] });

        Assert.True(view.Running);
        Assert.False(view.CanRun);
        Assert.Equal("Step 5 of 6", view.Eyebrow);
        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Done, StepState.Running, StepState.Pending], view.Steps.Select(s => s.State));
        Assert.Equal(("Typing into iRacing", "Typing !help into the chat"), (view.Steps[4].Title, view.Steps[4].Text));
    }

    [Fact]
    public void A_failed_self_test_names_the_step_and_can_run_again_while_sleeping()
    {
        var report = new SelfTestReport(false, [Ok(1), Ok(2), Ok(3), Ok(4), new SelfTestStep(5, "typed channel", false, "no reply to !help (admin rights?)")], Channels, null);
        var view = SelfTestView.From(Sleeping() with { LastSelfTest = report, LastSelfTestAt = DateTimeOffset.UnixEpoch });

        Assert.StartsWith("Failed at ", view.Eyebrow, StringComparison.Ordinal);
        Assert.Equal("Typing into iRacing: no reply to !help (admin rights?). Fix it, then run the self-test again.", view.Sentence);
        Assert.True(view.CanRun);
        Assert.Equal((StepState.Done, StatusCheck.Problem), (view.Steps[4].State, view.Steps[4].Check));
        Assert.Equal((StepState.Pending, "Not run"), (view.Steps[5].State, view.Steps[5].Text));
    }

    [Fact]
    public void Before_the_first_run_it_says_what_it_checks_and_once_connected_how_to_run_it_again()
    {
        var first = SelfTestView.From(Sleeping());
        Assert.Equal("Not run yet", first.Eyebrow);
        Assert.True(first.CanRun);
        Assert.All(first.Steps, s => Assert.Equal(StepState.Pending, s.State));

        Assert.False(SelfTestView.From(Sleeping() with { AgentId = null }).CanRun);

        var passed = new SelfTestReport(true, [Ok(1), Ok(2), Ok(3), Ok(4), Ok(5), new SelfTestStep(6, "macro channel", true, "disabled", SelfTestStepResult.Skipped)], Channels, null);
        var active = SelfTestView.From(Sleeping() with { State = AgentState.Active, LastSelfTest = passed });
        Assert.False(active.CanRun);
        Assert.EndsWith("put the agent to sleep and activate it.", active.Sentence, StringComparison.Ordinal);
        Assert.Equal(StatusCheck.Off, active.Steps[5].Check);
    }
}
