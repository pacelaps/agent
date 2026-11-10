using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Hosting;
using PaceLaps.Agent.Hosting.Tray;

namespace PaceLaps.Agent.Tests.Hosting;

public sealed class StatusViewTests
{
    private static readonly Uri Server = new("https://app.pacelaps.com/");
    private static readonly ChannelSet Ready = new(ChannelReadiness.Disabled, ChannelReadiness.Ready, ChannelReadiness.Ready);

    private static AgentStatus Sleeping() => new() { State = AgentState.Sleeping, AgentId = "agt_01", Server = Server, SimRunning = true };

    private static AgentStatus Active(bool executor = true) => new()
    {
        State = AgentState.Active, AgentId = "agt_01", Server = Server, ServerOnline = true, Realtime = true, IsExecutor = executor, Epoch = 3,
        SessionName = "Round 4", Channels = Ready, SimRunning = true, SimInSession = true, ClockOffset = TimeSpan.FromMilliseconds(12),
    };

    [Fact]
    public void Each_state_says_what_is_going_on_and_offers_the_next_step()
    {
        Assert.Equal(("Not paired yet", StatusAction.Pair), Head(Sleeping() with { AgentId = null }));
        Assert.Equal(("Sleeping", StatusAction.Activate), Head(Sleeping()));
        Assert.Equal(("Not activated", StatusAction.Activate), Head(Sleeping() with { Problem = "iRacing isn't in a session." }));
        Assert.Equal("iRacing isn't in a session.", StatusView.From(Sleeping() with { Problem = "iRacing isn't in a session." }).Sentence);
        Assert.Equal(("Self-test running", StatusAction.None), Head(Sleeping() with { State = AgentState.SelfTest }));
        Assert.Equal(("Ready", StatusAction.Sleep), Head(Active() with { State = AgentState.Ready, SessionName = null }));
        Assert.Equal(("In a session", StatusAction.Sleep), Head(Active()));
        Assert.Contains("types race control's commands into iRacing for Round 4", StatusView.From(Active()).Sentence, StringComparison.Ordinal);
        Assert.StartsWith("Standby for Round 4", StatusView.From(Active(executor: false)).Sentence, StringComparison.Ordinal);
        Assert.StartsWith("View only", StatusView.From(Active(executor: false) with { Channels = Ready with { Typed = ChannelReadiness.Disabled } }).Sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_come_first_in_red()
    {
        var offline = StatusView.From(Active() with { ServerOnline = false });
        Assert.Equal((TrayIcon.Problem, "Can't reach Race Control"), (offline.Tone, offline.Title));
        Assert.Equal(StatusCheck.Problem, offline.Rows.Single(r => r.Label == "Race Control").Check);

        Assert.Equal(("Typing into iRacing failed", StatusAction.Sleep), Head(Active() with { Channels = Ready with { Typed = ChannelReadiness.Failed } }));

        var failsafe = StatusView.From(Active() with { ServerOnline = false, LocalFailsafeSince = DateTimeOffset.UnixEpoch, LocalFailsafe = new LocalFailsafeSettings { Enabled = true } });
        Assert.Equal("Local failsafe running", failsafe.Title);
        Assert.Equal(StatusCheck.Problem, failsafe.LocalFailsafe.Check);
    }

    [Fact]
    public void The_rows_show_iracing_race_control_the_session_and_the_pairing()
    {
        Assert.Equal(
            [("iRacing", StatusCheck.Ok, "In a session"), ("Race Control", StatusCheck.Ok, "Connected"), ("Session", StatusCheck.Ok, "Round 4: sends the commands"),
             ("Pairing", StatusCheck.Ok, "Paired with app.pacelaps.com")],
            StatusView.From(Active()).Rows.Select(r => (r.Label, r.Check, r.Text)));
        Assert.Equal(
            [("iRacing", StatusCheck.Off, "Running, not in a session"), ("Race Control", StatusCheck.Off, "Not connected while sleeping"), ("Session", StatusCheck.Off, "None"),
             ("Pairing", StatusCheck.Ok, "Paired with app.pacelaps.com")],
            StatusView.From(Sleeping()).Rows.Select(r => (r.Label, r.Check, r.Text)));
        Assert.Equal(StatusCheck.Problem, StatusView.From(Active() with { SimRunning = false, SimInSession = false }).Rows[0].Check);
    }

    [Fact]
    public void The_last_self_test_is_listed_step_by_step()
    {
        var report = new SelfTestReport(false,
            [new SelfTestStep(1, "sim and telemetry", true, "build 2026.11.02.01"), new SelfTestStep(5, "typed channel", false, "no reply to !help (admin rights?)"),
             new SelfTestStep(6, "macro channel", true, "disabled", SelfTestStepResult.Skipped)], Ready, null);
        var view = StatusView.From(Sleeping() with { LastSelfTest = report, LastSelfTestAt = DateTimeOffset.UnixEpoch });

        Assert.StartsWith("Failed at ", view.SelfTestSummary, StringComparison.Ordinal);
        Assert.Equal([("iRacing", StatusCheck.Ok), ("Typing into iRacing", StatusCheck.Problem), ("Chat macros", StatusCheck.Off)],
            view.SelfTestSteps.Select(r => (r.Label, r.Check)));
        Assert.Equal("Not run yet. Activate runs it.", StatusView.From(Sleeping()).SelfTestSummary);
    }

    [Fact]
    public void Technical_terms_stay_in_the_details()
    {
        var view = StatusView.From(Active());
        Assert.Contains(view.Details, d => d.Contains("epoch 3", StringComparison.Ordinal));
        Assert.Contains(view.Details, d => d.Contains("live (websocket)", StringComparison.Ordinal));

        string[] internals = ["epoch", "observer", "channel", "websocket", "long-poll", "executor"];
        foreach (var status in new[] { Sleeping(), Sleeping() with { AgentId = null }, Active(), Active(executor: false), Active() with { ServerOnline = false } })
        {
            var v = StatusView.From(status);
            var texts = new[] { v.Title, v.Sentence, v.SelfTestSummary, v.LocalFailsafe.Text }.Concat(v.Rows.Select(r => r.Label + " " + r.Text));
            Assert.All(texts, text => Assert.DoesNotContain(internals, word => text.Contains(word, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void The_strip_is_green_only_while_all_is_well_in_a_session()
    {
        Assert.Equal(StatusStrip.Green, StatusView.From(Active()).Strip);
        Assert.Equal(StatusStrip.Amber, StatusView.From(Active() with { State = AgentState.Ready, SessionName = null }).Strip);
        Assert.Equal(StatusStrip.Grey, StatusView.From(Sleeping()).Strip);
        Assert.Equal(StatusStrip.Blue, StatusView.From(Sleeping() with { State = AgentState.SelfTest }).Strip);
        Assert.Equal(StatusStrip.Red, StatusView.From(Active() with { ServerOnline = false }).Strip);
        Assert.Equal(StatusStrip.Hazard, StatusView.From(Active() with { ServerOnline = false, LocalFailsafeSince = DateTimeOffset.UnixEpoch }).Strip);
    }

    [Fact]
    public void The_card_names_the_session_and_the_role_and_the_tiles_say_how_each_part_is()
    {
        var view = StatusView.From(Active() with { TrackName = "Okayama International Circuit", CarCount = 22 });
        Assert.Equal("Live · Okayama International Circuit", view.Eyebrow);
        Assert.Equal(("Session", "Round 4", "Sends the commands"), (view.SideLabel, view.SideName, view.Role));
        Assert.Equal(
            [("iRacing", StatusCheck.Ok, "In the session", "Admin rights · 22 cars"), ("Race Control", StatusCheck.Ok, "Connected", "Live updates"),
             ("Typing", StatusCheck.Ok, "Ready", "Without taking focus"), ("Local failsafe", StatusCheck.Off, "Off", "Turn it on in the settings")],
            view.Tiles.Select(t => (t.Label, t.Check, t.Value, t.Detail)));

        var asleep = StatusView.From(Sleeping());
        Assert.Equal(("Asleep · not connected to Race Control", (string?)null), (asleep.Eyebrow, asleep.SideName));
        Assert.Equal(StatusCheck.Problem, StatusView.From(Active() with { ServerOnline = false, EventsBuffered = 7 }).Tiles[1].Check);
        Assert.Equal("Retrying · 7 events waiting", StatusView.From(Active() with { ServerOnline = false, EventsBuffered = 7 }).Tiles[1].Detail);
        Assert.Equal(("Running", "Local failsafe v2"), (StatusView.From(Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch, LocalFailsafeHeld = "Local failsafe v2" }).SideLabel,
            StatusView.From(Active() with { LocalFailsafeSince = DateTimeOffset.UnixEpoch, LocalFailsafeHeld = "Local failsafe v2" }).SideName));
    }

    [Fact]
    public void Last_commands_show_what_was_typed_how_it_ended_and_how_long_it_took()
    {
        var at = new DateTimeOffset(2026, 11, 10, 20, 46, 58, TimeSpan.Zero);
        var commands = Enumerable.Range(0, 8).Select(i => new PaceLaps.Agent.Core.Commands.FinishedCommand($"c{i}", "pit_close", "!pitclose", CommandOutcome.Sent, null, at, TimeSpan.FromMilliseconds(161), false))
            .Prepend(new PaceLaps.Agent.Core.Commands.FinishedCommand("y", "yellow", "!yellow Full course caution", CommandOutcome.Verified, "via typed", at, TimeSpan.FromMilliseconds(166.4), false))
            .Prepend(new PaceLaps.Agent.Core.Commands.FinishedCommand("b", "black_flag", "!black #42 D", CommandOutcome.Failed, "typing failed", at, TimeSpan.FromMilliseconds(900), true))
            .ToList();

        var view = StatusView.From(Active() with { RecentCommands = commands });

        Assert.Equal(StatusView.CommandLines, view.Commands.Count);
        Assert.Equal(("!black #42 D", "Failed", StatusCheck.Problem, "900 ms", true, "typing failed"),
            (view.Commands[0].Text, view.Commands[0].Result, view.Commands[0].Check, view.Commands[0].Took, view.Commands[0].Local, view.Commands[0].Detail));
        Assert.Equal(("!yellow Full course caution", "Verified", StatusCheck.Ok, "166 ms", (string?)null),
            (view.Commands[1].Text, view.Commands[1].Result, view.Commands[1].Check, view.Commands[1].Took, view.Commands[1].Detail));
        Assert.Equal(at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), view.Commands[1].Time);
    }

    private static (string, StatusAction) Head(AgentStatus s)
    {
        var view = StatusView.From(s);
        return (view.Title, view.Action);
    }
}
