using System.Text.Json;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Commands;

public sealed class CommandExecutorTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;
    private readonly FakeSim _sim = new();
    private readonly FakeStatusSink _sink = new();
    private readonly FakeMacro _macro = new();
    private readonly FakeReplay _replay = new();
    private ChannelSet _channels = new(ChannelReadiness.Ready, ChannelReadiness.Ready, ChannelReadiness.Ready);
    private TimeSpan _offset = TimeSpan.Zero;
    private readonly CommandExecutor _executor;

    public CommandExecutorTests()
    {
        _desktop = new FakeDesktop(_clock);
        var typed = new TypedChatSender(_desktop, _desktop, _desktop, _clock, chatBox: _desktop);
        var macros = new MacroTable([(14, "!pitclose$"), (15, "!pacelaps +2$")]);
        _executor = new CommandExecutor(new CommandRenderer(), typed, _macro, macros, _replay, _sim, _clock, _sink,
            () => _channels, () => _offset)
        {
            Epoch = 7,
        };
    }

    private static CommandMessage Cmd(string id, string type, string paramsJson = "{}", long epoch = 7, DateTimeOffset? executeAt = null) => new()
    {
        Id = id,
        Epoch = epoch,
        Type = type,
        Params = JsonDocument.Parse(paramsJson).RootElement.Clone(),
        Idempotent = false,
        IssuedAt = new DateTimeOffset(2026, 11, 7, 19, 0, 0, TimeSpan.Zero),
        ExecuteAt = executeAt,
        IssuedBy = "steward:12",
    };

    private async Task Run(params CommandMessage[] commands)
    {
        foreach (var c in commands)
        {
            _executor.Submit(c);
        }

        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        await _executor.WaitForVerificationsAsync();
    }

    private static SimFrame Caution() => SimFixtures.Frame(flags: SimValues.FlagCautionWaving);

    [Fact]
    public async Task Yellow_goes_received_executing_and_verified_when_the_caution_shows()
    {
        _sim.FrameSource = () => _desktop.Typed.Contains("!yellow", StringComparison.Ordinal) ? Caution() : SimFixtures.Frame();

        await Run(Cmd("c1", "yellow"));

        Assert.Equal([CommandStep.ReceivedByExecutor, CommandStep.Executing, CommandStep.Result], _sink.For("c1").Select(s => s.Step));
        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Verified, result.Outcome);
        Assert.Contains("via typed; caution flag", result.Detail, StringComparison.Ordinal);
        Assert.Equal("!yellow", _desktop.Typed);
        Assert.NotNull(_executor.LastLatencyMs);
    }

    [Fact]
    public async Task Every_status_the_executor_sends_is_valid_against_the_contract()
    {
        await Run(Cmd("a", "yellow"), Cmd("b", "pit_close"), Cmd("c", "clear", """{"car":"x"}"""), Cmd("d", "black_flag", """{"car":"12","mode":"drive_through"}"""));

        Assert.NotEmpty(_sink.Statuses);
        foreach (var s in _sink.Statuses)
        {
            ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(s));
        }
    }

    [Fact]
    public async Task Yellow_is_not_thrown_again_while_a_caution_is_active()
    {
        _sim.FrameSource = Caution;

        await Run(Cmd("c1", "yellow"));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Verified, result.Outcome);
        Assert.Contains("caution already active", result.Detail, StringComparison.Ordinal);
        Assert.Equal("", _desktop.Typed);
    }

    [Fact]
    public async Task A_yellow_that_shows_no_effect_ends_at_sent_with_a_note()
    {
        await Run(Cmd("c1", "yellow"));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, result.Outcome);
        Assert.Contains("caution flag not seen within 5 s", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fixed_commands_use_a_ready_macro_slot_with_exactly_that_text()
    {
        await Run(Cmd("c1", "pit_close"));

        Assert.Equal([14], _macro.Triggered);
        Assert.Equal("", _desktop.Typed);
        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, result.Outcome);
        Assert.Equal("via macro slot 14; not verifiable in telemetry", result.Detail);
        Assert.Equal("channel macro slot 14", _sink.For("c1").Single(s => s.Step == CommandStep.Executing).Detail);
    }

    [Fact]
    public async Task Without_a_ready_macro_channel_fixed_commands_are_typed()
    {
        _channels = _channels with { Macro = ChannelReadiness.Unverified };

        await Run(Cmd("c1", "pit_close"));

        Assert.Empty(_macro.Triggered);
        Assert.Equal("!pitclose", _desktop.Typed);
    }

    [Fact]
    public async Task A_relative_pace_lap_command_never_uses_a_macro_even_if_a_slot_matches()
    {
        await Run(Cmd("c1", "pace_laps_add", """{"n":2}"""));

        Assert.Empty(_macro.Triggered);
        Assert.Equal("!pacelaps +2", _desktop.Typed);
    }

    [Fact]
    public async Task Rejects_an_older_epoch_without_executing()
    {
        await Run(Cmd("c1", "pit_open", epoch: 6));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal("stale epoch 6, current is 7", result.Detail);
        Assert.Equal("", _desktop.Typed);
        Assert.DoesNotContain(_sink.Statuses, s => s.Step == CommandStep.Executing);
    }

    [Fact]
    public async Task A_newer_epoch_is_accepted()
    {
        await Run(Cmd("c1", "pit_open", epoch: 8));

        Assert.Equal(CommandOutcome.Sent, _sink.ResultOf("c1").Outcome);
    }

    [Fact]
    public async Task A_command_id_received_again_is_not_executed_again_and_its_result_is_repeated()
    {
        await Run(Cmd("c1", "pace_laps_add", """{"n":2}"""));
        _sink.Statuses.Clear();

        await Run(Cmd("c1", "pace_laps_add", """{"n":2}"""));

        Assert.Equal("!pacelaps +2", _desktop.Typed);
        var repeated = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, repeated.Outcome);
        Assert.Contains("duplicate command id, not executed again", repeated.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_parameters_fail_with_the_reason()
    {
        await Run(Cmd("c1", "clear", """{"car":"12a"}"""));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.StartsWith("rejected: car must be one to three digits", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fails_when_the_typed_channel_is_not_ready()
    {
        _channels = _channels with { Typed = ChannelReadiness.Unverified };

        await Run(Cmd("c1", "clear", """{"car":"12"}"""));

        Assert.Equal("typed channel is unverified", _sink.ResultOf("c1").Detail);
        Assert.Equal("", _desktop.Typed);
    }

    [Fact]
    public async Task A_typed_failure_is_reported_with_its_reason()
    {
        _desktop.SimRefusesFocus = true;

        await Run(Cmd("c1", "clear", """{"car":"12"}"""));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.StartsWith("FocusFailed:", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Black_flag_is_verified_on_the_target_car()
    {
        _sim.FrameSource = () => _desktop.Typed.Contains("!black #12 D", StringComparison.Ordinal)
            ? SimFixtures.Frame(cars: [(1, SimFixtures.Car(carFlags: SimValues.FlagBlack))])
            : SimFixtures.Frame(cars: [(1, SimFixtures.Car())]);

        await Run(Cmd("c1", "black_flag", """{"car":"12","mode":"drive_through"}"""));

        Assert.Equal(CommandOutcome.Verified, _sink.ResultOf("c1").Outcome);
        Assert.Contains("black flag on #12", _sink.ResultOf("c1").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_black_flag_on_a_car_that_already_has_one_is_not_reported_as_verified()
    {
        _sim.FrameSource = () => SimFixtures.Frame(cars: [(1, SimFixtures.Car(carFlags: SimValues.FlagBlack))]);

        await Run(Cmd("c1", "black_flag", """{"car":"12","mode":"drive_through"}"""));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, result.Outcome);
        Assert.Contains("black flag on #12 was already showing before the command", result.Detail, StringComparison.Ordinal);
        Assert.Equal("!black #12 D", _desktop.Typed);
    }

    [Fact]
    public async Task Clear_is_verifiable_only_when_a_black_flag_was_showing()
    {
        await Run(Cmd("c1", "clear", """{"car":"12"}"""));
        Assert.Contains("not verifiable", _sink.ResultOf("c1").Detail, StringComparison.Ordinal);

        _sim.FrameSource = () => _desktop.Typed.EndsWith("!clear #12!clear #12", StringComparison.Ordinal)
            ? SimFixtures.Frame(cars: [(1, SimFixtures.Car())])
            : SimFixtures.Frame(cars: [(1, SimFixtures.Car(carFlags: SimValues.FlagBlack))]);
        await Run(Cmd("c2", "clear", """{"car":"12"}"""));
        Assert.Equal(CommandOutcome.Verified, _sink.ResultOf("c2").Outcome);
    }

    [Fact]
    public async Task Waits_for_execute_at_on_the_server_clock()
    {
        _offset = TimeSpan.FromSeconds(2);
        var start = _clock.UtcNow;

        // Server time is local + 2 s, so execute-at "server now + 1.5 s" is local now + 1.5 s.
        await Run(Cmd("c1", "pit_open", executeAt: start + _offset + TimeSpan.FromSeconds(1.5)));

        // It ran at local start + 1.5 s, and reports that on the server's clock.
        var executing = _sink.For("c1").Single(s => s.Step == CommandStep.Executing);
        Assert.Equal(start + TimeSpan.FromSeconds(1.5) + _offset, executing.At);
    }

    [Fact]
    public async Task A_restart_type_is_checked_only_during_a_caution_where_the_pace_mode_can_show_it()
    {
        var start = _clock.Elapsed;
        await Run(Cmd("c1", "restart_single"));

        // Under green: "sent" right after typing, no wait for the 10 s restart check.
        Assert.Equal(CommandOutcome.Sent, _sink.ResultOf("c1").Outcome);
        Assert.True(_clock.Elapsed - start < CommandVerifier.RestartTimeout, $"took {_clock.Elapsed - start}");
        Assert.Contains("not verifiable", _sink.ResultOf("c1").Detail, StringComparison.Ordinal);

        _sim.FrameSource = () => _desktop.Typed.Contains("!restart double", StringComparison.Ordinal)
            ? SimFixtures.Frame(flags: SimValues.FlagCautionWaving, paceMode: SimValues.PaceModeDoubleFileRestart)
            : Caution();
        await Run(Cmd("c2", "restart_double"));

        Assert.Equal(CommandOutcome.Verified, _sink.ResultOf("c2").Outcome);
    }

    [Fact]
    public async Task A_late_command_runs_at_once_with_a_note()
    {
        await Run(Cmd("c1", "pit_open", executeAt: _clock.UtcNow - TimeSpan.FromSeconds(3)));

        Assert.Contains("late by 3.0 s", _sink.ResultOf("c1").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_jump_uses_the_sdk_channel_only_when_ready()
    {
        await Run(Cmd("c1", "replay_jump", """{"sessionNum":1,"sessionTime":30.5}"""));
        Assert.Equal([(1, 30.5)], _replay.Jumps);
        Assert.Equal(CommandOutcome.Sent, _sink.ResultOf("c1").Outcome);

        _channels = _channels with { Sdk = ChannelReadiness.Unverified };
        await Run(Cmd("c2", "replay_jump", """{"sessionNum":1,"sessionTime":30.5}"""));
        Assert.Equal("sdk channel is unverified", _sink.ResultOf("c2").Detail);
    }

    [Fact]
    public async Task Replay_commands_follow_a_car_go_back_live_and_skip_the_epoch_check()
    {
        await Run(Cmd("c1", "replay_jump", """{"sessionNum":2,"sessionTime":807,"car":"18"}""", epoch: 3), Cmd("c2", "replay_live", epoch: 0));

        Assert.Equal([(2, 807.0)], _replay.Jumps);
        Assert.Equal(["18"], _replay.Cars);
        Assert.Equal(1, _replay.LiveCount);
        Assert.Equal(CommandOutcome.Sent, _sink.ResultOf("c1").Outcome);
        Assert.Equal(CommandOutcome.Sent, _sink.ResultOf("c2").Outcome);

        // A race command with the same old epoch is still refused.
        await Run(Cmd("c3", "pit_open", epoch: 3));
        Assert.Equal("stale epoch 3, current is 7", _sink.ResultOf("c3").Detail);
    }

    [Fact]
    public async Task A_second_Enter_shows_in_the_result_detail()
    {
        _desktop.IgnoreEnters = 1;

        await Run(Cmd("c1", "pit_open"));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, result.Outcome);
        Assert.Contains("second Enter sent the line", result.Detail, StringComparison.Ordinal);
        Assert.Equal(["!pitopen"], _desktop.SentLines);
    }

    [Fact]
    public async Task A_line_the_sim_did_not_send_fails_the_command()
    {
        _desktop.IgnoreEnters = 2;

        await Run(Cmd("c1", "eol", """{"car":"12"}"""));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.StartsWith("LineNotSent:", result.Detail, StringComparison.Ordinal);
        Assert.False(_desktop.ChatOpen);
    }

    [Fact]
    public async Task Commands_run_in_arrival_order()
    {
        await Run(Cmd("c1", "pit_open"), Cmd("c2", "clear", """{"car":"12"}"""), Cmd("c3", "announce", """{"text":"Green next lap"}"""));

        Assert.Equal("!pitopen!clear #12Green next lap", _desktop.Typed);
    }

    [Fact]
    public async Task Two_typed_channel_failures_in_a_row_report_the_channel_failed()
    {
        var failed = new List<string>();
        _executor.TypedChannelFailed += failed.Add;
        _desktop.SimRefusesFocus = true;

        _executor.Submit(Cmd("c1", "announce", """{"text":"one"}"""));
        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        Assert.Empty(failed);
        _executor.Submit(Cmd("c2", "announce", """{"text":"two"}"""));
        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);

        Assert.StartsWith("FocusFailed", Assert.Single(failed), StringComparison.Ordinal);
        Assert.Equal(CommandOutcome.Failed, _sink.ResultOf("c2").Outcome);
    }

    [Fact]
    public async Task A_success_between_failures_and_a_failure_of_one_command_do_not_count()
    {
        var failed = 0;
        _executor.TypedChannelFailed += _ => failed++;

        _desktop.SimRefusesFocus = true;
        _executor.Submit(Cmd("c1", "announce", """{"text":"one"}"""));
        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        _desktop.SimRefusesFocus = false;
        _executor.Submit(Cmd("c2", "announce", """{"text":"two"}"""));
        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        _desktop.SimRefusesFocus = true;
        _executor.Submit(Cmd("c3", "announce", """{"text":"three"}"""));
        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, failed);

        // The operator's own text in the chat box fails this command, not the channel.
        _desktop.SimRefusesFocus = false;
        _desktop.BoxTextAfterEnter = "gg !four";
        _executor.Submit(Cmd("c4", "announce", """{"text":"four"}"""));
        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        Assert.Equal(CommandOutcome.Failed, _sink.ResultOf("c4").Outcome);
        Assert.Equal(0, failed);
    }
}

public sealed class MacroTableTests
{
    [Fact]
    public void Matches_the_exact_text_with_or_without_the_dollar()
    {
        var t = new MacroTable([(3, "!yellow$"), (4, "!pitclose")]);

        Assert.Equal(3, t.SlotFor("!yellow"));
        Assert.Equal(4, t.SlotFor("!pitclose"));
        Assert.Null(t.SlotFor("!yellow Debris"));
    }

    [Fact]
    public void Rejects_bad_and_duplicate_slots()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MacroTable([(16, "!x$")]));
        Assert.Throws<ArgumentException>(() => new MacroTable([(3, "!a$"), (3, "!b$")]));
    }
}
