using System.Text.Json;
using PaceLaps.Agent.ConsoleHost.Cli;
using PaceLaps.Agent.ConsoleHost.Poc;
using PaceLaps.Agent.Core.Agent;
using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Poc;

public sealed class CommandShorthandTests
{
    private static (string Type, string Params) P(string line)
    {
        var r = CommandShorthand.Parse(line, out var error);
        Assert.True(r is not null, error);
        return (r.Value.Type, r.Value.Params.GetRawText());
    }

    [Theory]
    [InlineData("pit_close", "pit_close", "{}")]
    [InlineData("yellow", "yellow", "{}")]
    [InlineData("yellow Debris in turn 3", "yellow", """{"message":"Debris in turn 3"}""")]
    [InlineData("clear 12", "clear", """{"car":"12"}""")]
    [InlineData("pace_laps_add 2", "pace_laps_add", """{"n":2}""")]
    [InlineData("black_flag 12 drive_through", "black_flag", """{"car":"12","mode":"drive_through"}""")]
    [InlineData("black_flag 12 stop_and_go 10", "black_flag", """{"car":"12","mode":"stop_and_go","seconds":10}""")]
    [InlineData("eol 4 Back of the line", "eol", """{"car":"4","message":"Back of the line"}""")]
    [InlineData("announce Green flag next lap", "announce", """{"text":"Green flag next lap"}""")]
    [InlineData("replay_jump 1 30.5", "replay_jump", """{"sessionNum":1,"sessionTime":30.5}""")]
    [InlineData("replay_jump 1 30.5 18", "replay_jump", """{"sessionNum":1,"sessionTime":30.5,"car":"18"}""")]
    [InlineData("replay_live", "replay_live", "{}")]
    [InlineData("json pace_laps_set {\"n\":4}", "pace_laps_set", """{"n":4}""")]
    public void Parses_lines_into_server_style_commands(string line, string type, string json)
    {
        var (t, p) = P(line);

        Assert.Equal(type, t);
        Assert.Equal(json, p);
    }

    [Fact]
    public void Leaves_validation_to_the_renderer()
    {
        // A bad value still parses; the agent's renderer rejects it exactly as for a server command.
        var (_, p) = P("pace_laps_set four");
        Assert.Equal("""{"n":"four"}""", p);
    }

    [Theory]
    [InlineData("kick 12", "unknown command")]
    [InlineData("json yellow", "usage")]
    [InlineData("json yellow {bad", "invalid JSON")]
    public void Reports_errors(string line, string reason)
    {
        Assert.Null(CommandShorthand.Parse(line, out var error));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parses_the_macro_option()
    {
        var t = RunCommand.ParseMacros("14=!pitclose; 15=!pitopen$");

        Assert.Equal(14, t.SlotFor("!pitclose"));
        Assert.Equal(15, t.SlotFor("!pitopen"));
        Assert.Equal(0, RunCommand.ParseMacros(null).Count);
        Assert.Throws<CliException>(() => RunCommand.ParseMacros("16=!x"));
        Assert.Throws<CliException>(() => RunCommand.ParseMacros("!x"));
        Assert.Throws<CliException>(() => RunCommand.ParseMacros("3=!a;3=!b"));
    }
}

public sealed class PocRunTests : IDisposable
{
    private readonly string _outbox = Path.Combine(Path.GetTempPath(), $"poc-{Guid.NewGuid():N}.jsonl");

    public void Dispose() => File.Delete(_outbox);

    [Fact]
    public async Task A_session_through_the_stub_server_produces_only_contract_valid_messages()
    {
        var clock = new FakeClock();
        var desktop = new FakeDesktop(clock);
        var sim = new FakeSim();
        sim.FrameSource = () => desktop.Typed.Contains("!yellow", StringComparison.Ordinal) ? SimFixtures.Frame(flags: SimValues.FlagCaution) : SimFixtures.Frame();
        using (var server = new StubServer(_outbox, clock, TimeSpan.FromMilliseconds(50), registeredLeague: null, executor: true))
        {
            var host = new AgentHost(new AgentHostOptions { AgentId = "agt_poc", AgentVersion = "test" }, sim, server,
                new TypedChatSender(desktop, desktop, desktop, clock), new FakeMacro(), new MacroTable([(14, "!pitclose$")]), null,
                new FakePrompt(true, true, true), clock);

            await host.ActivateAsync(TestContext.Current.CancellationToken);
            for (var i = 0; i < 12; i++)
            {
                host.Tick();
                clock.Advance(TimeSpan.FromMilliseconds(100));
            }

            server.Deliver(Command("cmd_1", "yellow", host.Executor.Epoch));
            server.Deliver(Command("cmd_2", "clear", host.Executor.Epoch, """{"car":"99x"}"""));
            await host.Executor.ProcessQueuedAsync(TestContext.Current.CancellationToken);
            await host.Executor.WaitForVerificationsAsync();
            host.Tick();
        }

        var lines = File.ReadAllLines(_outbox).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        var schemaFor = new Dictionary<string, string>
        {
            ["hello"] = "agent-messages.schema.json",
            ["heartbeat"] = "agent-messages.schema.json",
            ["event"] = "agent-messages.schema.json",
            ["command_status"] = "agent-messages.schema.json",
            ["snapshot"] = "snapshot.schema.json",
            ["clock_ping"] = "device-messages.schema.json",
            ["self_test_result"] = "agent-messages.schema.json",
        };
        var checkedTypes = new HashSet<string>();
        foreach (var l in lines.Where(l => l.GetProperty("dir").GetString() == "out"))
        {
            var type = l.GetProperty("type").GetString()!;
            ContractSchemas.AssertValid(schemaFor[type], l.GetProperty("message").GetRawText());
            checkedTypes.Add(type);
        }

        Assert.Equal(schemaFor.Keys.ToHashSet(), checkedTypes);
        var text = File.ReadAllText(_outbox);
        Assert.DoesNotContain("Example Operator", text, StringComparison.Ordinal);
        Assert.Contains("(redacted, 16 characters)", text, StringComparison.Ordinal);

        var results = lines.Where(l => l.GetProperty("type").GetString() == "command_status")
            .Select(l => l.GetProperty("message"))
            .Where(m => m.GetProperty("step").GetString() == "result")
            .ToDictionary(m => m.GetProperty("commandId").GetString()!, m => m.GetProperty("outcome").GetString());
        Assert.Equal("verified", results["cmd_1"]);
        Assert.Equal("failed", results["cmd_2"]);
    }

    private static CommandMessage Command(string id, string type, long epoch, string paramsJson = "{}") => new()
    {
        Id = id, Epoch = epoch, Type = type, Params = JsonDocument.Parse(paramsJson).RootElement.Clone(), Idempotent = true,
        IssuedAt = DateTimeOffset.UnixEpoch, IssuedBy = "steward:console",
    };
}
