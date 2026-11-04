using System.Text.Json;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Commands;

/// <summary>
/// Typed commands the posted way (spec-feedback 37): a quickly verifiable command that telemetry does not show within
/// the posted check is typed once more the focus way, inside the queue, so it never overtakes a later command.
/// </summary>
public sealed class PostedRepeatTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;
    private readonly FakeSim _sim = new();
    private readonly FakeStatusSink _sink = new();
    private readonly CommandExecutor _executor;

    public PostedRepeatTests()
    {
        _desktop = new FakeDesktop(_clock);
        var typed = new TypedChatSender(_desktop, _desktop, _desktop, _clock, chatBox: _desktop, poster: _desktop);
        var ready = new ChannelSet(ChannelReadiness.Ready, ChannelReadiness.Ready, ChannelReadiness.Ready);
        _executor = new CommandExecutor(new CommandRenderer(), typed, new FakeMacro(), MacroTable.Empty, new FakeReplay(), _sim, _clock, _sink,
            () => ready, () => TimeSpan.Zero)
        {
            Epoch = 7,
        };

        // Car #12 shows a black flag while the last line the sim sent for it was a black flag.
        _sim.FrameSource = () =>
        {
            var last = _desktop.SentLines.LastOrDefault(l => l.Contains("#12", StringComparison.Ordinal));
            var black = last?.StartsWith("!black", StringComparison.Ordinal) == true;
            return SimFixtures.Frame(cars: [(1, SimFixtures.Car(carFlags: black ? SimValues.FlagBlack : 0))]);
        };
    }

    private static CommandMessage Cmd(string id, string type, string paramsJson = "{}") => new()
    {
        Id = id,
        Epoch = 7,
        Type = type,
        Params = JsonDocument.Parse(paramsJson).RootElement.Clone(),
        Idempotent = false,
        IssuedAt = new DateTimeOffset(2026, 11, 7, 19, 0, 0, TimeSpan.Zero),
        IssuedBy = "steward:12",
    };

    private const string Black = """{"car":"12","mode":"drive_through"}""";

    private async Task Run(params CommandMessage[] commands)
    {
        foreach (var c in commands)
        {
            _executor.Submit(c);
        }

        await _executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        await _executor.WaitForVerificationsAsync();
    }

    [Fact]
    public async Task A_posted_black_flag_that_telemetry_shows_is_verified_without_the_focus()
    {
        await Run(Cmd("c1", "black_flag", Black));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Verified, result.Outcome);
        Assert.Equal("via typed; black flag on #12", result.Detail);
        Assert.DoesNotContain(_desktop.Calls, c => c.StartsWith("front:", StringComparison.Ordinal));
        Assert.Equal(["!black #12 D"], _desktop.SentLines);
    }

    [Fact]
    public async Task A_lost_posted_black_flag_is_typed_once_more_the_focus_way()
    {
        _desktop.LosePostedLines = 1;
        var start = _clock.Elapsed;

        await Run(Cmd("c1", "black_flag", Black));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Verified, result.Outcome);
        Assert.Contains("repeated with the focus switch: black flag on #12 not seen 1.5 s after the line", result.Detail, StringComparison.Ordinal);
        Assert.Equal(["!black #12 D"], _desktop.SentLines);
        Assert.Equal(1, _desktop.Calls.Count(c => c == "post-enter"));
        Assert.Contains("front:sim", _desktop.Calls);
        Assert.True(_clock.Elapsed - start >= TimeSpan.FromSeconds(1.5));
        Assert.False(_desktop.ChatOpen);
    }

    [Fact]
    public async Task An_effect_that_shows_within_the_check_is_not_repeated()
    {
        // Telemetry shows the flag one second after the line.
        TimeSpan? sentAt = null;
        _sim.FrameSource = () =>
        {
            if (_desktop.SentLines.Count > 0)
            {
                sentAt ??= _clock.Elapsed;
            }

            var black = sentAt is { } at && _clock.Elapsed - at >= TimeSpan.FromSeconds(1);
            return SimFixtures.Frame(cars: [(1, SimFixtures.Car(carFlags: black ? SimValues.FlagBlack : 0))]);
        };

        await Run(Cmd("c1", "black_flag", Black));

        Assert.Equal(CommandOutcome.Verified, _sink.ResultOf("c1").Outcome);
        Assert.DoesNotContain("front:sim", _desktop.Calls);
    }

    [Fact]
    public async Task A_repeat_happens_before_the_next_command_so_the_order_holds()
    {
        _desktop.LosePostedLines = 1;

        await Run(Cmd("c1", "black_flag", Black), Cmd("c2", "clear", """{"car":"12"}"""));

        // The black flag (repeated the focus way) went before the clear: #12 ends without a black flag.
        Assert.Equal(["!black #12 D", "!clear #12"], _desktop.SentLines);
        Assert.Equal(CommandOutcome.Verified, _sink.ResultOf("c1").Outcome);
        Assert.Equal(CommandOutcome.Verified, _sink.ResultOf("c2").Outcome);
        Assert.Equal(0u, _sim.Frame!.Cars[1].SessionFlags & SimValues.FlagBlack);
    }

    [Fact]
    public async Task A_command_telemetry_cannot_check_is_sent_once_and_not_repeated()
    {
        _desktop.LosePostedLines = 1;

        await Run(Cmd("c1", "announce", """{"text":"Hello"}"""));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, result.Outcome);
        Assert.Contains("not verifiable", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("front:sim", _desktop.Calls);
    }

    [Fact]
    public async Task A_restart_type_is_not_repeated_its_effect_takes_longer()
    {
        _desktop.LosePostedLines = 1;
        _sim.FrameSource = () => SimFixtures.Frame(flags: SimValues.FlagCautionWaving);

        await Run(Cmd("c1", "restart_single"));

        var result = _sink.ResultOf("c1");
        Assert.Equal(CommandOutcome.Sent, result.Outcome);
        Assert.Contains("not seen within 10 s", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("front:sim", _desktop.Calls);
    }

    [Fact]
    public async Task A_refused_post_fails_the_command_and_counts_against_the_channel()
    {
        _desktop.RejectPosts = true;
        string? failed = null;
        _executor.TypedChannelFailed += reason => failed = reason;

        await Run(Cmd("c1", "pit_close"), Cmd("c2", "pit_open"));

        Assert.Equal(CommandOutcome.Failed, _sink.ResultOf("c1").Outcome);
        Assert.StartsWith("PostRejected:", _sink.ResultOf("c1").Detail, StringComparison.Ordinal);
        Assert.NotNull(failed);
    }
}
