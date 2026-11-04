using System.Text.Json;
using PaceLaps.Agent.Core.Agent;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Agent;

public sealed class AgentHostTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;
    private readonly FakeSim _sim = new();
    private readonly FakeServer _server;
    private readonly AgentHost _host;

    public AgentHostTests()
    {
        _desktop = new FakeDesktop(_clock);
        _server = new FakeServer(_clock);
        _host = new AgentHost(new AgentHostOptions { AgentId = "agt_01", AgentVersion = "0.1.0-test" }, _sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, null, new FakePrompt(), _clock);
    }

    private static CommandMessage Cmd(string id, string type, string paramsJson = "{}", long epoch = 1) => new()
    {
        Id = id, Epoch = epoch, Type = type, Params = JsonDocument.Parse(paramsJson).RootElement.Clone(), Idempotent = true,
        IssuedAt = DateTimeOffset.UnixEpoch, IssuedBy = "steward:1",
    };

    private Task<PaceLaps.Agent.Core.SelfTest.SelfTestReport?> Activate() => _host.ActivateAsync(TestContext.Current.CancellationToken);

    private void Ticks(int n)
    {
        for (var i = 0; i < n; i++)
        {
            _host.Tick();
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }
    }

    private sealed class SavedMacros(IReadOnlyDictionary<int, string> slots) : PaceLaps.Agent.Core.SelfTest.IMacroConfigSource
    {
        public IReadOnlyDictionary<int, string>? ReadSlots() => slots;

        public string Location => "app.ini (test)";
    }

    [Fact]
    public async Task The_leagues_macros_policy_switches_the_macro_channel_off_and_back_to_what_the_self_test_verified()
    {
        var ct = TestContext.Current.CancellationToken;
        var macro = new FakeMacro();
        var host = new AgentHost(new AgentHostOptions { AgentId = "agt_01", AgentVersion = "0.1.0-test", SelfTest = new() { MacroEnabled = true } }, _sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), macro, new MacroTable([(14, "!pitclose")]), null, new FakePrompt(), _clock,
            macroConfig: new SavedMacros(new Dictionary<int, string> { [14] = "!pitclose$" }));
        await host.ActivateAsync(ct);
        Assert.Equal(ChannelReadiness.Ready, host.Channels.Macro);

        // The league switches macros off: the channel is reported disabled and the command is typed.
        _server.Push(_server.View with { Macros = MacrosPolicy.Off });
        Assert.Equal((ChannelReadiness.Disabled, MacrosPolicy.Off), (host.Channels.Macro, host.MacrosPolicy));
        _server.Deliver(Cmd("c1", "pit_close"));
        await host.Executor.ProcessQueuedAsync(ct);
        Assert.Empty(macro.Triggered);
        Assert.EndsWith("!pitclose", _desktop.Typed, StringComparison.Ordinal);

        // Allowed again: back to the verified macro channel without a new self-test.
        _server.Push(_server.View with { Macros = MacrosPolicy.Allowed });
        Assert.Equal(ChannelReadiness.Ready, host.Channels.Macro);
        _server.Deliver(Cmd("c2", "pit_close"));
        await host.Executor.ProcessQueuedAsync(ct);
        Assert.Equal([14], macro.Triggered);

        // A view that says nothing about it (a server before step 5) changes nothing.
        _server.Push(_server.View);
        Assert.Equal(MacrosPolicy.Allowed, host.MacrosPolicy);
    }

    [Fact]
    public void Sleeping_sends_nothing_and_ignores_commands()
    {
        Ticks(30);
        _server.Deliver(Cmd("c1", "yellow"));

        Assert.Empty(_server.Sent);
        Assert.Equal("", _desktop.Typed);
    }

    [Fact]
    public async Task Activation_runs_the_self_test_then_a_hello_whose_answer_makes_it_active()
    {
        await Activate();

        Assert.Equal(AgentState.Active, _host.States.State);
        var hello = Assert.Single(_server.Hellos);
        Assert.Equal(AgentState.Ready, hello.State);
        Assert.Equal(new SessionChangeData(12345, 6789, 111, 222, 2, "Race"), hello.Session);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(hello));
        var hellos = new[] { hello };
        Assert.Equal("Example Operator", hellos[0].Operator!.Name);
        Assert.Equal(ChannelReadiness.Ready, hellos[0].Channels!.Typed);
        Assert.Equal(1, _host.Executor.Epoch);
        Assert.True(_host.IsExecutor);
    }

    [Fact]
    public async Task A_failed_self_test_goes_back_to_sleep_and_stays_silent()
    {
        _server.AnswerPings = false;

        var report = await Activate();

        Assert.False(report!.Passed);
        Assert.Equal(AgentState.Sleeping, _host.States.State);
        Assert.StartsWith("step 3", _host.States.LastFailure, StringComparison.Ordinal);
        // The server answered the session check: its audit log gets the result, and then nothing more.
        Assert.False(Assert.IsType<SelfTestResultMessage>(Assert.Single(_server.Sent)).Passed);
        Ticks(20);
        Assert.Single(_server.Sent);
    }

    [Fact]
    public async Task Active_sends_snapshots_at_2_Hz_and_heartbeats_at_1_Hz_all_valid()
    {
        await Activate();
        _server.Sent.Clear();

        Ticks(30);

        var snapshots = _server.SentOf<SnapshotMessage>().ToList();
        var heartbeats = _server.SentOf<HeartbeatMessage>().ToList();
        Assert.Equal(6, snapshots.Count);
        Assert.Equal(3, heartbeats.Count);
        Assert.Equal([1L, 2, 3, 4, 5, 6], snapshots.Select(s => s.Seq));
        Assert.All(heartbeats, h => Assert.Equal(AgentState.Active, h.State));
        foreach (var s in snapshots)
        {
            ContractSchemas.AssertValid("snapshot.schema.json", ContractJson.Serialize(s));
        }

        foreach (var h in heartbeats)
        {
            ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(h));
        }
    }

    [Fact]
    public async Task Events_get_sequence_numbers_and_stay_buffered_until_acknowledged()
    {
        await Activate();
        Ticks(1);
        _sim.FrameSource = () => SimFixtures.Frame(flags: SimValues.FlagCautionWaving);
        Ticks(1);
        _sim.Session = SimFixtures.Session(updateCount: 2, cars: [(1, "12", 2), (2, "33", 0), (3, "4", 0)]);
        _sim.FrameSource = () => SimFixtures.Frame(flags: SimValues.FlagCautionWaving);
        Ticks(1);

        var events = _server.SentOf<AgentEventMessage>().ToList();
        Assert.Equal([EventType.FlagChange, EventType.Incident], events.Select(e => e.EventType));
        // Numbered from the start time, so a restarted agent never repeats a sequence number.
        var first = _clock.UtcNow.ToUnixTimeMilliseconds() - 300;
        Assert.True(events[0].Seq >= first - 1000 && events[0].Seq <= first + 1000, $"seq {events[0].Seq}");
        Assert.Equal(events[0].Seq + 1, events[1].Seq);
        Assert.Equal(2, _host.Events.Count);
        foreach (var e in events)
        {
            ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(e));
        }
    }

    [Fact]
    public async Task An_active_agent_sends_iRacings_results_when_the_session_info_changes_them()
    {
        await Activate();
        var row = new SimResultRow(1, 1, 0, 1, 0, 0, 60.1, 60.1, 1, 1, 0, 0, "Running");
        _sim.Session = SimFixtures.Session(updateCount: 2) with { Results = [new SimSessionResults(2, false, 1, null, [row])] };
        Ticks(2);
        _sim.Session = SimFixtures.Session(updateCount: 3) with { Results = [new SimSessionResults(2, true, 1, null, [row with { Incidents = 2 }])] };
        Ticks(1);

        var results = _server.SentOf<AgentEventMessage>().Where(e => e.EventType == EventType.SessionResults).ToList();
        Assert.Equal([false, true], results.Select(e => ((SessionResultsData)e.Data).Official));
        Assert.Equal(2, ((SessionResultsData)results[1].Data).Cars[0].Incidents);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(results[1]));
    }

    /// <summary>A sim that also hands out every frame, as the iRacing adapter does.</summary>
    private sealed class FramesSim : ISimData, ISimFrames
    {
        public bool IsConnected => true;
        public SimSession? Session { get; set; }
        public SimFrame? Frame { get; set; }

        public event Action<SimFrame>? FrameArrived;

        public void Arrive(SimFrame frame)
        {
            Frame = frame;
            FrameArrived?.Invoke(frame);
        }
    }

    [Fact]
    public async Task Sector_crossings_measured_at_every_frame_are_sent_bundled_once_a_second()
    {
        var sim = new FramesSim
        {
            Session = SimFixtures.Session() with
            {
                Track = SimTrack.From(353, "Lime Rock Park", "Grand Prix", "2.46 km")! with { SectorStarts = [0, 0.5] },
            },
        };
        SimFrame At(double t, float pct, int lap) => SimFixtures.Frame(time: t, cars: [(1, SimFixtures.Car(lap, pct))]);
        sim.Arrive(At(100, 0.40f, 5));
        var host = new AgentHost(new AgentHostOptions { AgentId = "agt_01", AgentVersion = "0.1.0-test" }, sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, null, new FakePrompt(), _clock);
        await host.ActivateAsync(TestContext.Current.CancellationToken);
        host.Tick(); // the session the frames belong to
        _server.Sent.Clear();
        _clock.Advance(TimeSpan.FromMilliseconds(100));

        // Frames at 60 Hz between two loop ticks: the car passes half way and then the line.
        for (var i = 0; i <= 60; i++)
        {
            sim.Arrive(At(100 + i / 60.0, 0.45f + i * 0.01f, 0.45f + i * 0.01f >= 1 ? 6 : 5));
        }

        for (var i = 0; i < 12; i++)
        {
            host.Tick();
            _clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        var events = _server.SentOf<AgentEventMessage>().Where(e => e.EventType == EventType.SectorCrossings).ToList();
        var e = Assert.Single(events);
        var data = Assert.IsType<SectorCrossingsData>(e.Data);
        Assert.Equal([1, 0], data.Crossings.Select(c => c.Sector));
        Assert.Equal(6, data.Crossings[1].Lap);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(e));
    }

    [Fact]
    public async Task Commands_are_executed_only_while_active_and_executor()
    {
        _sim.FrameSource = () => _desktop.Typed.Contains("!yellow", StringComparison.Ordinal) ? SimFixtures.Frame(flags: SimValues.FlagCaution) : SimFixtures.Frame();
        await Activate();

        _server.Deliver(Cmd("c1", "yellow"));
        await _host.Executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        await _host.Executor.WaitForVerificationsAsync();

        var result = _server.SentOf<CommandStatusMessage>().Last(s => s.CommandId == "c1");
        Assert.Equal(CommandOutcome.Verified, result.Outcome);
        Assert.Equal("!help!yellow", _desktop.Typed);

        _host.AssignExecutor(2, isExecutor: false);
        _server.Deliver(Cmd("c2", "pit_close", epoch: 2));
        Assert.Equal("this agent is not the executor", _server.SentOf<CommandStatusMessage>().Last().Detail);
    }

    [Fact]
    public async Task An_active_agent_that_is_not_the_executor_runs_replays()
    {
        var replay = new FakeReplay();
        var host = new AgentHost(new AgentHostOptions { AgentId = "agt_01", AgentVersion = "0.1.0-test" }, _sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, replay, new FakePrompt(), _clock);
        await host.ActivateAsync(TestContext.Current.CancellationToken);
        host.AssignExecutor(2, isExecutor: false);

        _server.Deliver(Cmd("r1", "replay_jump", """{"sessionNum":2,"sessionTime":807,"car":"7"}""", epoch: 0));
        _server.Deliver(Cmd("r2", "replay_live", epoch: 0));
        await host.Executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["7"], replay.Cars);
        Assert.Equal(1, replay.LiveCount);
        Assert.Equal(CommandOutcome.Sent, _server.SentOf<CommandStatusMessage>().Last(s => s.CommandId == "r2").Outcome);
    }

    [Fact]
    public async Task A_ready_agent_that_could_not_join_answers_commands_as_failed()
    {
        _server.View = new ServerView(false, null, null, 0, false, "session not started");
        await Activate();

        _server.Deliver(Cmd("c1", "pit_close"));

        Assert.Equal(AgentState.Ready, _host.States.State);
        var status = Assert.Single(_server.SentOf<CommandStatusMessage>());
        Assert.Equal(CommandOutcome.Failed, status.Outcome);
        Assert.Equal("agent is ready but not active in a session", status.Detail);
    }

    [Fact]
    public async Task Sleep_stops_all_traffic_and_drops_queued_commands()
    {
        await Activate();
        _server.Deliver(Cmd("c1", "pit_close"));
        _host.Sleep();
        _server.Sent.Clear();

        Ticks(20);
        _server.Deliver(Cmd("c2", "pit_open"));
        await _host.Executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(_server.Sent);
        Assert.Equal("!help", _desktop.Typed);
        Assert.Equal(new ChannelSet(ChannelReadiness.Disabled, ChannelReadiness.Disabled, ChannelReadiness.Disabled), _host.Channels);
    }

    [Fact]
    public async Task Losing_the_sim_keeps_the_state_and_heartbeats_report_not_in_session()
    {
        await Activate();
        _server.Sent.Clear();
        _sim.IsConnected = false;
        _sim.Session = null;

        Ticks(15);

        Assert.Equal(AgentState.Active, _host.States.State);
        Assert.Empty(_server.SentOf<SnapshotMessage>());
        Assert.All(_server.SentOf<HeartbeatMessage>(), h => Assert.False(h.InSession));
    }

    [Fact]
    public async Task The_operator_selected_league_is_reported_in_snapshots()
    {
        var host = new AgentHost(new AgentHostOptions { AgentId = "a", AgentVersion = "v", SelfTest = new() { LeagueOverride = 4242 } }, _sim, _server,
            new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, null, new FakePrompt(), _clock);
        _sim.Session = SimFixtures.Session(leagueId: 0);

        await host.ActivateAsync(TestContext.Current.CancellationToken);
        host.Tick();

        Assert.Equal(4242, _server.SentOf<SnapshotMessage>().Single().Session.LeagueId);
    }

    [Fact]
    public async Task A_waiting_agent_becomes_active_when_a_heartbeat_result_says_so_and_ready_again_when_released()
    {
        _server.View = new ServerView(false, null, null, 0, false, "Waiting for race control");
        await Activate();
        Assert.Equal(AgentState.Ready, _host.States.State);
        Assert.Equal("Waiting for race control", _host.LastView!.Reason);

        _server.Push(new ServerView(true, "12", "Race 1", 2, true));
        Assert.Equal(AgentState.Active, _host.States.State);
        Assert.Equal(2, _host.Executor.Epoch);
        Assert.True(_host.IsExecutor);

        _server.Push(new ServerView(null, "12", null, 3, false));
        Assert.Equal(AgentState.Active, _host.States.State);
        Assert.False(_host.IsExecutor);
        Assert.Equal(3, _host.Executor.Epoch);

        _server.Push(new ServerView(false, null, null, 0, false, "The sim is not in a session."));
        Assert.Equal(AgentState.Ready, _host.States.State);
    }

    [Fact]
    public async Task The_resync_reports_the_outage_on_the_servers_clock()
    {
        await Activate();
        var from = _clock.UtcNow;
        _server.LastOutage = new OutageWindow(from, from.AddSeconds(40));

        _server.Reconnect();

        var outage = Assert.Single(_server.SentOf<ResyncMessage>()).Outage!;
        Assert.NotEqual(TimeSpan.Zero, _host.ClockOffset);
        Assert.Equal(new OutageWindow(from + _host.ClockOffset, from.AddSeconds(40) + _host.ClockOffset), outage);
    }

    [Fact]
    public async Task An_executor_that_can_no_longer_type_reports_its_typed_channel_failed_until_the_next_self_test()
    {
        var ct = TestContext.Current.CancellationToken;
        await Activate();
        _desktop.SimRefusesFocus = true;
        _server.Deliver(Cmd("c1", "announce", """{"text":"one"}"""));
        _server.Deliver(Cmd("c2", "announce", """{"text":"two"}"""));
        await _host.Executor.ProcessQueuedAsync(ct);

        Assert.Equal(ChannelReadiness.Failed, _host.Channels.Typed);
        Ticks(1);
        Assert.Equal(ChannelReadiness.Failed, _server.SentOf<HeartbeatMessage>().Last().Channels!.Typed);

        // The operator fixes it and runs the self-test again.
        _desktop.SimRefusesFocus = false;
        _host.Sleep();
        await Activate();
        Assert.Equal(ChannelReadiness.Ready, _host.Channels.Typed);
    }

    [Fact]
    public async Task Through_a_handover_and_back_only_commands_of_the_current_epoch_are_executed()
    {
        var ct = TestContext.Current.CancellationToken;
        await Activate();
        Assert.Equal((1L, true), (_host.Executor.Epoch, _host.IsExecutor));

        // The server hands over to another agent (epoch 2): this one rejects what still reaches it.
        _server.Push(new ServerView(true, "s_1", "Race 1", 2, false));
        _server.Deliver(Cmd("late1", "pit_close", epoch: 1));
        Assert.Equal("this agent is not the executor", _server.SentOf<CommandStatusMessage>().Last(s => s.CommandId == "late1").Detail);

        // It becomes executor again (epoch 3): a command of epoch 1 arriving late (long-poll) is stale, epoch 3 runs.
        _server.Push(new ServerView(true, "s_1", "Race 1", 3, true));
        _server.Deliver(Cmd("late2", "pit_close", epoch: 1));
        _server.Deliver(Cmd("now", "pit_open", epoch: 3));
        await _host.Executor.ProcessQueuedAsync(ct);

        Assert.Equal("stale epoch 1, current is 3", _server.SentOf<CommandStatusMessage>().Last(s => s.CommandId == "late2").Detail);
        Assert.Equal(CommandOutcome.Sent, _server.SentOf<CommandStatusMessage>().Last(s => s.CommandId == "now").Outcome);
        Assert.DoesNotContain("!pitclose", _desktop.Typed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_a_reconnect_the_unacknowledged_events_are_resent()
    {
        await Activate();
        Ticks(1);
        _sim.FrameSource = () => SimFixtures.Frame(flags: SimValues.FlagCautionWaving);
        Ticks(1);
        _sim.Session = SimFixtures.Session(updateCount: 2, cars: [(1, "12", 2), (2, "33", 0), (3, "4", 0)]);
        Ticks(1);
        var events = _server.SentOf<AgentEventMessage>().ToList();
        _server.Acknowledge(events[0].Seq);

        _server.Reconnect();

        var resync = Assert.Single(_server.SentOf<ResyncMessage>());
        Assert.Equal(events[0].Seq, resync.LastAckedSeq);
        Assert.Equal([events[1].Seq], resync.Events!.Select(e => e.Seq));
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(resync));
    }

    [Fact]
    public async Task In_an_outage_the_local_failsafe_throws_the_caution_reports_it_afterwards_and_a_steward_releases_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new MemoryLocalFailsafeStore
        {
            Copy = new LocalFailsafeCopy("local-failsafe-default", 1, new string('1', 64), false, false,
                LocalFailsafe.LocalFailsafeCopiesTests.TemplateDocument().GetRawText()),
        };
        var host = new AgentHost(new AgentHostOptions
            {
                AgentId = "agt_01", AgentVersion = "0.1.0-test", LocalFailsafe = new() { Enabled = true, DelaySeconds = 30 },
            }, _sim, _server, new TypedChatSender(_desktop, _desktop, _desktop, _clock), null, MacroTable.Empty, null, new FakePrompt(), _clock,
            localFailsafeStore: store);
        await host.ActivateAsync(ct);
        var typedBefore = _desktop.Typed.Length;

        // The server goes quiet. 29 s: nothing; 31 s: the executor (rank 0) starts.
        _clock.Advance(TimeSpan.FromSeconds(29));
        host.Tick();
        Assert.False(host.LocalFailsafe.Running);
        _clock.Advance(TimeSpan.FromSeconds(2));
        host.Tick();
        Assert.True(host.LocalFailsafe.Running);

        await host.Executor.ProcessQueuedAsync(ct);
        host.Tick();
        await host.Executor.ProcessQueuedAsync(ct);
        _sim.FrameSource = () => SimFixtures.Frame(flags: SimValues.FlagCaution, cars: (0, SimFixtures.Car()));
        _clock.Advance(TimeSpan.FromSeconds(3));
        host.Tick();

        var typed = _desktop.Typed[typedBefore..];
        Assert.True(typed.IndexOf("!restart single", StringComparison.Ordinal) is >= 0 and var i && typed.IndexOf("!yellow", StringComparison.Ordinal) > i, typed);
        Assert.True(_server.SentOf<HeartbeatMessage>().Last().LocalFailsafe!.Running);
        var sent = _server.SentOf<AgentEventMessage>().Where(e => e.EventType == EventType.LocalFailsafe).ToList();
        Assert.Equal(3, sent.Count);
        foreach (var e in sent)
        {
            ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(e));
        }

        // The server is back: the resync carries the run's events, and the steward releases it.
        _server.Reconnect();
        Assert.Equal(3, Assert.Single(_server.SentOf<ResyncMessage>()).Events!.Count(e => e.EventType == EventType.LocalFailsafe));
        _server.Push(_server.View with { ReleaseLocalFailsafe = true });

        Assert.False(host.LocalFailsafe.Running);
        Assert.Equal(4, _server.SentOf<AgentEventMessage>().Count(e => e.EventType == EventType.LocalFailsafe));
    }

    [Fact]
    public async Task An_older_epoch_that_crosses_a_newer_assignment_does_not_take_the_role_back()
    {
        await Activate();
        _server.Push(new ServerView(null, "s_1", "Race 1", 3, true));

        _server.Push(new ServerView(true, "s_1", "Race 1", 2, false));
        Assert.Equal((3L, true), (_host.Executor.Epoch, _host.IsExecutor));

        // Another session starts its own epochs.
        _server.Push(new ServerView(true, "s_2", "Race 2", 1, false));
        Assert.Equal((1L, false), (_host.Executor.Epoch, _host.IsExecutor));
    }

    [Fact]
    public async Task Events_and_command_reports_carry_the_servers_clock()
    {
        await Activate();
        Assert.NotEqual(TimeSpan.Zero, _host.ClockOffset);
        _server.Deliver(Cmd("c1", "pit_close", epoch: _host.Executor.Epoch));
        await _host.Executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
        Ticks(1);
        _sim.FrameSource = () => SimFixtures.Frame(flags: SimValues.FlagCautionWaving);
        var before = _clock.UtcNow;
        Ticks(1);

        var e = _server.SentOf<AgentEventMessage>().Last();
        Assert.Equal(before + _host.ClockOffset, e.EventTime);
        var result = _server.SentOf<CommandStatusMessage>().Last(s => s.CommandId == "c1");
        Assert.True(result.At > _clock.UtcNow - TimeSpan.FromSeconds(5) + _host.ClockOffset - TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Before_sleeping_it_says_so_once_and_ignores_answers_meanwhile()
    {
        await Activate();

        Assert.True(await _host.SayGoodbyeAsync(TimeSpan.FromSeconds(3)));
        _server.Push(new ServerView(false, null, null, 2, false, "released"));
        _host.Sleep();

        var last = _server.SentOf<HeartbeatMessage>().Last();
        Assert.Equal((AgentState.Sleeping, false), (last.State, last.InSession));
        Assert.Equal(AgentState.Sleeping, _host.States.State);
        ContractSchemas.AssertValid("agent-messages.schema.json", ContractJson.Serialize(last));
        Assert.False(await _host.SayGoodbyeAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Sleeping_ignores_the_servers_view()
    {
        await Activate();
        _host.Sleep();

        _server.Push(new ServerView(true, "12", "Race 1", 5, true));

        Assert.Equal(AgentState.Sleeping, _host.States.State);
        Assert.False(_host.IsExecutor);
    }
}
