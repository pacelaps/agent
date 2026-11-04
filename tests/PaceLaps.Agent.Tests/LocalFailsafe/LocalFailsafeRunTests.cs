using System.Text.Json;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Tests.Contracts;

namespace PaceLaps.Agent.Tests.LocalFailsafe;

public sealed class LocalFailsafeRunTests
{
    private static readonly LocalFacts Green = LocalFacts.Of(["green"], paceCarOnTrack: false);
    private static readonly LocalFacts Caution = LocalFacts.Of(["caution"], paceCarOnTrack: true);
    private static readonly LocalFacts OneToGo = LocalFacts.Of(["caution", "one_lap_to_green"], paceCarOnTrack: true);

    private static LocalProcedure Template() =>
        LocalProcedure.Parse(JsonDocument.Parse(ContractSchemas.Example("procedure.local-failsafe.json")).RootElement).Procedure!;

    private static (LocalProcedure? Procedure, string? Error) Parse(string json) => LocalProcedure.Parse(JsonDocument.Parse(json).RootElement);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void The_template_throws_the_caution_and_keeps_it_alive_at_one_lap_to_green_until_it_ends()
    {
        var run = new LocalFailsafeRun(Template());

        Assert.Equal("restart_single", run.Advance(S(0), Green)!.Type);
        Assert.Null(run.Advance(S(1), Green));
        run.CommandDone();
        var yellow = run.Advance(S(1), Green)!;
        Assert.Equal("yellow", yellow.Type);
        Assert.Contains("race control lost connection", yellow.Params.GetProperty("message").GetString(), StringComparison.Ordinal);
        run.CommandDone();

        // Under caution, not yet at one to go: it waits.
        Assert.Null(run.Advance(S(2), Caution));
        Assert.Null(run.Advance(S(60), Caution));
        Assert.Equal("keepalive", run.Phase);

        // One to go: one pace lap, and not another until the flag has cleared.
        var extend = run.Advance(S(90), OneToGo)!;
        Assert.Equal(("pace_laps_add", 1), (extend.Type, extend.Params.GetProperty("n").GetInt32()));
        run.CommandDone();
        Assert.Null(run.Advance(S(91), OneToGo));
        Assert.Null(run.Advance(S(95), Caution));
        Assert.Equal("keepalive", run.Phase);
        Assert.Equal("pace_laps_add", run.Advance(S(180), OneToGo)!.Type);
        run.CommandDone();

        // Green: the run ends.
        Assert.Null(run.Advance(S(300), Green));
        Assert.True(run.Ended);
        Assert.Equal("end", run.EndReason);
    }

    [Fact]
    public void A_failed_command_does_not_stop_the_run()
    {
        var run = new LocalFailsafeRun(Template());
        run.Advance(S(0), Green);

        // The driver reports it done whatever the outcome: nobody is there to decide.
        run.CommandDone();

        Assert.Equal("yellow", run.Advance(S(1), Green)!.Type);
    }

    [Fact]
    public void Waits_branches_and_the_end_of_a_phase_work_like_the_servers_engine()
    {
        var (procedure, error) = Parse("""
            {"id": "t", "phases": [
              {"id": "a", "blocks": [
                {"type": "wait", "params": {"seconds": 10}},
                {"type": "branch", "params": {"condition": {"all": [{"flag_is": "caution"}, {"not": {"pace_car_on_track": false}}]}, "then": "b", "else": "c"}}]},
              {"id": "b", "blocks": [{"type": "pits", "params": {"state": "closed"}}, {"type": "announce", "params": {"text": "pits closed"}}]},
              {"id": "c", "blocks": [{"type": "end"}]}
            ]}
            """);
        Assert.Null(error);
        var run = new LocalFailsafeRun(procedure!);

        Assert.Null(run.Advance(S(0), Caution));
        Assert.Null(run.Advance(S(9.9), Caution));
        Assert.Equal("pit_close", run.Advance(S(10), Caution)!.Type);
        run.CommandDone();
        Assert.Equal("pits closed", run.Advance(S(10), Caution)!.Params.GetProperty("text").GetString());
        run.CommandDone();

        Assert.Null(run.Advance(S(11), Caution));
        Assert.Equal(("b", "phase finished"), (run.Phase, run.EndReason));
    }

    [Fact]
    public void A_loop_without_a_wait_ends_the_run()
    {
        var run = new LocalFailsafeRun(Parse("""{"id": "t", "phases": [{"id": "a", "blocks": [{"type": "goto", "params": {"phase": "a"}}]}]}""").Procedure!);

        Assert.Null(run.Advance(S(0), Green));
        Assert.StartsWith("more than 200 blocks", run.EndReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stopped_run_does_nothing_more()
    {
        var run = new LocalFailsafeRun(Template());
        run.Stop("released by a steward");

        Assert.Null(run.Advance(S(0), Green));
        Assert.Equal("released by a steward", run.EndReason);
    }

    [Theory]
    [InlineData("""{"id": "t", "phases": [{"id": "a", "blocks": [{"type": "show_message", "params": {"text": "x"}}]}]}""", "block show_message cannot run offline")]
    [InlineData("""{"id": "t", "phases": [{"id": "a", "blocks": [{"type": "wait_steward", "params": {}}]}]}""", "block wait_steward cannot run offline")]
    [InlineData("""{"id": "t", "phases": [{"id": "a", "blocks": [{"type": "wait_until", "params": {"condition": {"steward_connected": false}}}]}]}""", "condition steward_connected cannot be evaluated offline")]
    [InlineData("""{"id": "t", "phases": [{"id": "a", "blocks": [{"type": "goto", "params": {"phase": "nowhere"}}]}]}""", "phase nowhere does not exist")]
    [InlineData("""{"id": "t", "phases": []}""", "not a procedure with phases")]
    [InlineData("""{"name": "no phases"}""", "not a procedure with phases")]
    public void Documents_that_cannot_run_offline_are_refused_with_the_reason(string json, string reason)
    {
        var (procedure, error) = Parse(json);

        Assert.Null(procedure);
        Assert.Equal(reason, error);
    }
}
