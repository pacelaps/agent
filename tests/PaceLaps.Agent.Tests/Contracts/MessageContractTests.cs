using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;

namespace PaceLaps.Agent.Tests.Contracts;

/// <summary>Every message the agent sends is serialized and validated against the shared schema.</summary>
public sealed class MessageContractTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 7, 19, 3, 11, 250, TimeSpan.Zero);
    private static readonly ChannelSet Channels = new(ChannelReadiness.Ready, ChannelReadiness.Ready, ChannelReadiness.Unverified);

    [Fact]
    public void Hello_is_valid()
    {
        var json = ContractJson.Serialize(new HelloMessage
        {
            AgentId = "agt_01",
            State = AgentState.Ready,
            AgentVersion = "0.1.0",
            Operator = new OperatorInfo(100200, "Example Operator"),
            Channels = Channels,
        });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
        Assert.Contains("\"state\":\"ready\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sdk\":\"unverified\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentState.Sleeping, "sleeping")]
    [InlineData(AgentState.SelfTest, "self_test")]
    [InlineData(AgentState.Active, "active")]
    public void Heartbeat_is_valid_in_every_state(AgentState state, string wire)
    {
        var json = ContractJson.Serialize(new HeartbeatMessage
        {
            AgentId = "agt_01", Epoch = 7, State = state, InSession = true, Channels = Channels, LastCommandLatencyMs = 310,
        });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
        Assert.Contains($"\"state\":\"{wire}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Events_of_every_type_are_valid()
    {
        object[] data =
        [
            new IncidentData("12", 2, 6, 2, 863.2, 15, 0.62),
            new FlagChangeData(["caution", "caution_waving"], ["green"], 2, 900.5),
            new SessionChangeData(12345, 6789, 111, 222, 2, "Race"),
            new SectorCrossingsData(2, [new SectorCrossingData("12", 0, 15, 911.214), new SectorCrossingData("33", 1, 14, 911.9, Pit: true, Jump: true)]),
        ];
        EventType[] types = [EventType.Incident, EventType.FlagChange, EventType.SessionChange, EventType.SectorCrossings];

        for (var i = 0; i < data.Length; i++)
        {
            var json = ContractJson.Serialize(new AgentEventMessage { AgentId = "agt_01", Seq = 311 + i, EventTime = T, EventType = types[i], Data = data[i] });
            ContractSchemas.AssertValid("agent-messages.schema.json", json);
        }
    }

    [Fact]
    public void An_incident_event_without_its_data_fields_is_rejected()
    {
        var json = ContractJson.Serialize(new AgentEventMessage { AgentId = "a", Seq = 1, EventTime = T, EventType = EventType.Incident, Data = new { car = "12" } });

        Assert.NotEmpty(ContractSchemas.Errors("agent-messages.schema.json", json));
    }

    [Theory]
    [InlineData(CommandStep.ReceivedByExecutor, null, null)]
    [InlineData(CommandStep.Executing, null, "typed")]
    [InlineData(CommandStep.Result, CommandOutcome.Sent, "not verifiable")]
    [InlineData(CommandStep.Result, CommandOutcome.Verified, null)]
    [InlineData(CommandStep.Result, CommandOutcome.Failed, "FocusFailed: iRacing did not come to the front")]
    public void Command_status_steps_are_valid(CommandStep step, CommandOutcome? outcome, string? detail)
    {
        var json = ContractJson.Serialize(new CommandStatusMessage { CommandId = "cmd_8f2", Step = step, Outcome = outcome, At = T, Detail = detail });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
    }

    [Fact]
    public void A_failed_result_without_detail_is_rejected_by_the_schema()
    {
        var json = ContractJson.Serialize(new CommandStatusMessage { CommandId = "c", Step = CommandStep.Result, Outcome = CommandOutcome.Failed, At = T });

        Assert.NotEmpty(ContractSchemas.Errors("agent-messages.schema.json", json));
    }

    [Fact]
    public void Resync_with_events_is_valid()
    {
        var e = new AgentEventMessage { AgentId = "a", Seq = 5, EventTime = T, EventType = EventType.FlagChange, Data = new FlagChangeData(["green"], [], 1, 2) };
        var json = ContractJson.Serialize(new ResyncMessage { LastAckedSeq = 4, Events = [e] });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
    }

    [Fact]
    public void Clock_ping_is_valid_and_pong_example_parses()
    {
        ContractSchemas.AssertValid("device-messages.schema.json", ContractJson.Serialize(new ClockPingMessage { ClientSentAt = T }));

        var pong = ContractJson.Deserialize<ClockPongMessage>(ContractSchemas.Example("clock_pong.json"))!;
        Assert.Equal(TimeSpan.FromMilliseconds(40), pong.ServerTime - pong.ClientSentAt);
    }

    [Fact]
    public void Snapshot_is_valid()
    {
        var json = ContractJson.Serialize(new SnapshotMessage
        {
            AgentId = "agt_01",
            Seq = 1042,
            CapturedAt = T,
            Session = new SnapshotSession
            {
                LeagueId = 12345, SeasonId = 6789, SessionId = 111, SubSessionId = 222, Num = 2, Type = "Race", State = "Racing",
                TimeSec = 812.4, Flags = ["caution", "one_lap_to_green"],
            },
            Cars =
            [
                new SnapshotCar { Num = "12", CustId = 100200, ClassId = 4098, Lap = 14, LapDistPct = 0.431, OnPitRoad = false, Surface = "OnTrack", PosClass = 3, PosOverall = 7, IsPaceCar = false, Incidents = 4, Connected = true },
                new SnapshotCar { Num = "0", ClassId = 11, Lap = 0, LapDistPct = 0, OnPitRoad = true, Surface = "InPitStall", PosClass = 0, PosOverall = 0, IsPaceCar = true, Connected = true },
            ],
        });

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        Assert.Contains("\"capturedAt\":\"2026-11-07T19:03:11.250Z\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_with_a_raw_sim_flag_name_is_rejected()
    {
        var json = ContractJson.Serialize(new SnapshotMessage
        {
            AgentId = "a", Seq = 1, CapturedAt = T,
            Session = new SnapshotSession { LeagueId = 0, SeasonId = 0, SessionId = 0, SubSessionId = 0, Num = 0, Type = "Race", State = "Racing", TimeSec = 1, Flags = ["startHidden"] },
            Cars = [],
        });

        Assert.NotEmpty(ContractSchemas.Errors("snapshot.schema.json", json));
    }

    [Fact]
    public void A_hello_result_without_a_session_reads_as_not_active_with_the_reason()
    {
        // Receive-only: the agent never writes it, so the null session (left out when writing) does not matter.
        var view = Core.Server.ServerView.From(ContractJson.Deserialize<ServerViewMessage>(ContractSchemas.Example("hello_result.waiting.json"))!);

        Assert.Equal(false, view.Active);
        Assert.Null(view.SessionId);
        Assert.Contains("no open session", view.Reason);
    }

    [Theory]
    [InlineData("hello.json", typeof(HelloMessage), "agent-messages.schema.json")]
    [InlineData("heartbeat.json", typeof(HeartbeatMessage), "agent-messages.schema.json")]
    [InlineData("command_status.json", typeof(CommandStatusMessage), "agent-messages.schema.json")]
    [InlineData("event.incident.json", typeof(AgentEventMessage), "agent-messages.schema.json")]
    [InlineData("snapshot.json", typeof(SnapshotMessage), "snapshot.schema.json")]
    [InlineData("snapshot.caution.json", typeof(SnapshotMessage), "snapshot.schema.json")]
    [InlineData("clock_ping.json", typeof(ClockPingMessage), "device-messages.schema.json")]
    [InlineData("clock_pong.json", typeof(ClockPongMessage), "device-messages.schema.json")]
    [InlineData("session_check.json", typeof(SessionCheckMessage), "agent-messages.schema.json")]
    [InlineData("session_check_result.json", typeof(SessionCheckResultMessage), "agent-server.schema.json")]
    [InlineData("hello_result.json", typeof(ServerViewMessage), "agent-server.schema.json")]
    [InlineData("heartbeat_result.json", typeof(ServerViewMessage), "agent-server.schema.json")]
    [InlineData("executor_assignment.json", typeof(ServerViewMessage), "agent-server.schema.json")]
    [InlineData("events_ack.json", typeof(EventsAckMessage), "agent-server.schema.json")]
    [InlineData("commands.json", typeof(CommandListMessage), "agent-server.schema.json")]
    [InlineData("pairing_request.json", typeof(PairingRequestMessage), "agent-pairing.schema.json")]
    [InlineData("pairing_code.json", typeof(PairingCodeMessage), "agent-pairing.schema.json")]
    [InlineData("pairing_poll.json", typeof(PairingPollMessage), "agent-pairing.schema.json")]
    [InlineData("pairing_status.json", typeof(PairingAnswerMessage), "agent-pairing.schema.json")]
    [InlineData("pairing_token.json", typeof(PairingAnswerMessage), "agent-pairing.schema.json")]
    [InlineData("hello_result.step5.json", typeof(ServerViewMessage), "agent-server.schema.json")]
    [InlineData("heartbeat_result.release-local-failsafe.json", typeof(ServerViewMessage), "agent-server.schema.json")]
    [InlineData("local_failsafe.json", typeof(LocalFailsafeDocumentMessage), "agent-server.schema.json")]
    [InlineData("heartbeat.local-failsafe-running.json", typeof(HeartbeatMessage), "agent-messages.schema.json")]
    [InlineData("self_test_result.json", typeof(SelfTestResultMessage), "agent-messages.schema.json")]
    [InlineData("self_test_result.failed.json", typeof(SelfTestResultMessage), "agent-messages.schema.json")]
    [InlineData("event.local_failsafe.json", typeof(AgentEventMessage), "agent-messages.schema.json")]
    [InlineData("resync.outage.json", typeof(ResyncMessage), "agent-messages.schema.json")]
    public void Example_documents_round_trip_through_the_agents_types(string example, Type type, string schema)
    {
        var parsed = JsonSerializer.Deserialize(ContractSchemas.Example(example), type, ContractJson.Options)!;
        var json = JsonSerializer.Serialize(parsed, type, ContractJson.Options);

        ContractSchemas.AssertValid(schema, json);
    }

    [Fact]
    public void A_heartbeat_holding_no_local_failsafe_writes_version_and_hash_as_null()
    {
        var json = ContractJson.Serialize(new HeartbeatMessage
        {
            AgentId = "agt_01", Epoch = 2, State = AgentState.Active, LocalFailsafe = LocalFailsafeState.None(enabled: true),
        });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
        Assert.Contains("\"localFailsafe\":{\"enabled\":true,\"version\":null,\"hash\":null,\"running\":false}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_local_failsafe_needs_its_start()
    {
        var running = new LocalFailsafeState { Enabled = true, Version = 1, Hash = new string('a', 64), Running = true };

        Assert.NotEmpty(ContractSchemas.Errors("agent-messages.schema.json",
            ContractJson.Serialize(new HeartbeatMessage { AgentId = "a", Epoch = 0, State = AgentState.Active, LocalFailsafe = running })));
        ContractSchemas.AssertValid("agent-messages.schema.json",
            ContractJson.Serialize(new HeartbeatMessage { AgentId = "a", Epoch = 0, State = AgentState.Active, LocalFailsafe = running with { Since = T } }));
    }

    [Theory]
    [InlineData(LocalFailsafeAction.Started, null, null)]
    [InlineData(LocalFailsafeAction.Command, "yellow", CommandOutcome.Verified)]
    [InlineData(LocalFailsafeAction.Ended, null, null)]
    [InlineData(LocalFailsafeAction.Released, null, null)]
    public void Local_failsafe_events_are_valid(LocalFailsafeAction action, string? command, CommandOutcome? result)
    {
        var data = new LocalFailsafeEventData { Action = action, Procedure = "local-failsafe-default", Version = 1, Command = command, Result = result };
        var json = ContractJson.Serialize(new AgentEventMessage { AgentId = "agt_01", Seq = 9, EventTime = T, EventType = EventType.LocalFailsafe, Data = data });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
        Assert.Contains("\"eventType\":\"local_failsafe\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_test_results_are_valid()
    {
        var json = ContractJson.Serialize(new SelfTestResultMessage
        {
            AgentId = "agt_01", At = T, Passed = false, Channels = Channels,
            Steps =
            [
                new SelfTestStepReport(1, SelfTestStepName.Sim, SelfTestStepResult.Pass),
                new SelfTestStepReport(5, SelfTestStepName.TypedChannel, SelfTestStepResult.Fail, "the operator is not an admin in this session"),
                new SelfTestStepReport(6, SelfTestStepName.MacroChannel, SelfTestStepResult.Unverified),
            ],
        });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
        Assert.Contains("\"name\":\"typed_channel\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resync_carries_the_outage_window()
    {
        var json = ContractJson.Serialize(new ResyncMessage { LastAckedSeq = 4, Outage = new OutageWindow(T, T.AddSeconds(40)), Events = [] });

        ContractSchemas.AssertValid("agent-messages.schema.json", json);
    }

    [Fact]
    public void Step_5_answers_read_into_the_view()
    {
        var hello = Core.Server.ServerView.From(ContractJson.Deserialize<ServerViewMessage>(ContractSchemas.Example("hello_result.step5.json"))!);
        var none = Core.Server.ServerView.From(ContractJson.Deserialize<ServerViewMessage>(ContractSchemas.Example("heartbeat_result.no-local-failsafe.json"))!);
        var release = Core.Server.ServerView.From(ContractJson.Deserialize<ServerViewMessage>(ContractSchemas.Example("heartbeat_result.release-local-failsafe.json"))!);
        var before = Core.Server.ServerView.From(ContractJson.Deserialize<ServerViewMessage>(ContractSchemas.Example("heartbeat_result.json"))!);

        Assert.Equal(MacrosPolicy.Off, hello.Macros);
        Assert.Equal(("local-failsafe-default", 1), (hello.LocalFailsafe!.Procedure, hello.LocalFailsafe.Version));
        Assert.True(none.LocalFailsafeKnown);
        Assert.Null(none.LocalFailsafe);
        Assert.True(release.ReleaseLocalFailsafe);
        Assert.Equal(0, release.LocalFailsafeRank);
        // A server before step 5 says nothing: the agent keeps what it has.
        Assert.False(before.LocalFailsafeKnown);
        Assert.Null(before.Macros);
    }

    [Theory]
    [InlineData("command.yellow.json", "yellow")]
    [InlineData("command.black_flag.json", "black_flag")]
    public void Command_examples_parse(string example, string type)
    {
        var command = ContractJson.Deserialize<CommandMessage>(ContractSchemas.Example(example))!;

        Assert.Equal(type, command.Type);
        Assert.Equal(JsonValueKind.Object, command.ParamsOrEmpty.ValueKind);
        Assert.True(command.Epoch > 0);
    }
}

public sealed class ContractCopyTests
{
    /// <summary>
    /// The copy in contracts/ must equal webservice/contracts when that repository sits next to this one
    /// (the developer machine). CI has only this repository and skips the comparison.
    /// </summary>
    [Fact]
    public void The_copied_contracts_match_the_web_service()
    {
        var repo = FindRepoRoot();
        var source = Path.Combine(Path.GetDirectoryName(repo)!, "webservice", "contracts");
        if (!Directory.Exists(source))
        {
            Assert.Skip("webservice repository not next to this one");
        }

        var copy = Path.Combine(repo, "contracts");
        var differences = new List<string>();
        foreach (var file in Directory.GetFiles(source, "*.json", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var mine = Path.Combine(copy, rel);
            if (!File.Exists(mine))
            {
                differences.Add($"missing: {rel}");
            }
            else if (Normalise(File.ReadAllText(mine)) != Normalise(File.ReadAllText(file)))
            {
                differences.Add($"differs: {rel}");
            }
        }

        Assert.True(differences.Count == 0, "Run scripts/sync-contracts.ps1. " + string.Join(", ", differences));
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PaceLaps.Agent.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }
}
