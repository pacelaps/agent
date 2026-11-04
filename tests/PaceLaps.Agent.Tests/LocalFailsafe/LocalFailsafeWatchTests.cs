using System.Text.Json;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.LocalFailsafe;

public sealed class LocalFailsafeWatchTests
{
    private static readonly LocalFacts Green = LocalFacts.Of(["green"], paceCarOnTrack: false);
    private static readonly LocalFacts Caution = LocalFacts.Of(["caution"], paceCarOnTrack: true);
    private static readonly LocalFacts OneToGo = LocalFacts.Of(["caution", "one_lap_to_green"], paceCarOnTrack: true);
    private readonly FakeClock _clock = new();
    private readonly List<LocalFailsafeEventData> _events = [];
    private readonly List<string> _sent = [];
    private readonly Queue<CommandOutcome> _outcomes = new();
    private readonly MemoryLocalFailsafeStore _store = new();
    private LocalFailsafeWatch _watch;

    public LocalFailsafeWatchTests()
    {
        _store.Copy = new LocalFailsafeCopy("local-failsafe-default", 1, new string('1', 64), ArmPractice: false, ArmQualify: false,
            LocalFailsafeCopiesTests.TemplateDocument().GetRawText());
        _watch = Watch(enabled: true);
    }

    private LocalFailsafeWatch Watch(bool enabled) => new(new LocalFailsafeSettings { Enabled = enabled, DelaySeconds = 30 },
        new LocalFailsafeCopies(new FakeServer(_clock), _store, _clock), Execute, _events.Add, _clock);

    private Task<(CommandOutcome Outcome, string? Detail)> Execute(string type, JsonElement parameters)
    {
        _sent.Add(type);
        var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : CommandOutcome.Verified;
        return Task.FromResult((outcome, outcome == CommandOutcome.Failed ? (string?)"FocusFailed: test" : null));
    }

    /// <summary>One tick, this long without the server.</summary>
    private void Tick(double sinceServer, LocalFacts? facts = null, int rank = 0, bool armed = true)
    {
        var f = facts ?? Green;
        _watch.Tick(new LocalFailsafeTick(TimeSpan.FromSeconds(sinceServer), rank, armed, f.Flags.Contains("caution"), f));
    }

    private IEnumerable<string> Actions => _events.Select(e => e.Action + (e.Command is null ? "" : $" {e.Command} {e.Result}"));

    [Fact]
    public void It_waits_for_the_delay_plus_the_stagger_of_its_rank()
    {
        Tick(29.9);
        Tick(40, rank: 1);
        Assert.False(_watch.Running);

        Tick(45, rank: 1);
        Assert.True(_watch.Running);
        Assert.Equal(["restart_single"], _sent);
        Assert.Equal(["Started"], Actions);
        Assert.Equal("no server contact for 45 s", _events[0].Detail);
        Assert.Equal((true, 1, true), (_watch.State.Running, _watch.State.Version, _watch.State.Since is not null));
    }

    [Fact]
    public void It_does_not_act_while_a_caution_is_out_but_does_once_it_is_over()
    {
        Tick(60, Caution);
        Assert.False(_watch.Running);

        Tick(70, Green);
        Assert.True(_watch.Running);
    }

    [Fact]
    public void Off_unarmed_or_without_a_copy_it_does_nothing()
    {
        _watch = Watch(enabled: false);
        Tick(300);
        _watch = Watch(enabled: true);
        Tick(300, armed: false);
        _store.Copy = null;
        _watch = Watch(enabled: true);
        Tick(300);

        Assert.Empty(_sent);
        Assert.Empty(_events);
    }

    [Fact]
    public void A_whole_run_throws_the_caution_keeps_it_alive_and_ends_with_it()
    {
        Tick(30);
        Tick(31);
        Tick(31, Caution);
        Tick(60, Caution);
        Tick(90, OneToGo);
        Tick(91, OneToGo);
        Tick(95, Caution);
        Tick(200, Green);
        Tick(201, Green);

        Assert.Equal(["restart_single", "yellow", "pace_laps_add"], _sent);
        Assert.Equal(["Started", "Command restart_single Verified", "Command yellow Verified", "Command pace_laps_add Verified", "Ended"], Actions);
        Assert.Equal("end", _events[^1].Detail);
        Assert.False(_watch.Running);
        Assert.Null(_watch.State.Since);
    }

    [Fact]
    public void A_failed_idempotent_command_is_tried_once_more_then_the_run_goes_on()
    {
        _outcomes.Enqueue(CommandOutcome.Failed);
        _outcomes.Enqueue(CommandOutcome.Failed);
        Tick(30);
        Tick(31);
        Assert.Equal(["restart_single"], _sent);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Tick(33);
        Tick(34);
        Tick(35);

        Assert.Equal(["restart_single", "restart_single", "yellow"], _sent);
        Assert.Equal("Command restart_single Failed", Actions.ElementAt(1));
        Assert.Equal("FocusFailed: test", _events[1].Detail);
    }

    [Fact]
    public void A_failed_command_that_is_not_idempotent_is_not_tried_again()
    {
        Tick(30);
        Tick(31);
        Tick(31, Caution);
        _outcomes.Enqueue(CommandOutcome.Failed);
        Tick(90, OneToGo);
        Tick(91, OneToGo);
        _clock.Advance(TimeSpan.FromSeconds(5));
        Tick(96, OneToGo);

        Assert.Single(_sent, "pace_laps_add");
        Assert.Contains("Command pace_laps_add Failed", Actions);
    }

    [Fact]
    public void One_run_per_outage_until_the_server_has_been_back()
    {
        Tick(30);
        Tick(31);
        Tick(31, Caution);
        Tick(40, Green);
        Tick(41, Green);
        Assert.False(_watch.Running);

        // Still no server, green flag racing: it does not throw another caution.
        Tick(120, Green);
        Assert.False(_watch.Running);

        _watch.ServerBack();
        Tick(30, Green);
        Assert.True(_watch.Running);
    }

    [Fact]
    public void A_steward_releases_it_and_it_never_ends_by_itself_otherwise()
    {
        Tick(30);
        Tick(31);
        Tick(31, Caution);
        _watch.ServerBack();
        Tick(0, Caution);
        Assert.True(_watch.Running);

        _watch.Release();

        Assert.False(_watch.Running);
        Assert.Equal("Released", Actions.Last());
        _watch.Release();
        Assert.Single(_events, e => e.Action == LocalFailsafeAction.Released);
    }

    [Fact]
    public void Going_to_sleep_stops_it_silently()
    {
        Tick(30);
        var before = _events.Count;

        _watch.Stop();

        Assert.False(_watch.Running);
        Assert.Equal(before, _events.Count);
    }
}
