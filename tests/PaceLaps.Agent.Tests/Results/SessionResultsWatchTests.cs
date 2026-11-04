using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Results;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Results;

/// <summary>iRacing's results into session_results events (spec-feedback 42), with the sim check's values.</summary>
public sealed class SessionResultsWatchTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 3, 15, 26, 28, TimeSpan.Zero);

    // The session's cars: 12 (idx 1), 33 (idx 2), 4 (idx 3); the pace car is idx 0.
    private static SimResultRow Row(int idx, int pos, int classPos, int laps, int behind, double gap, double last = 60.5, double best = 59.8,
        int bestLap = 2, int led = 0, int incidents = 0, int outId = 0, string outText = "Running") =>
        new(idx, pos, classPos, laps, behind, gap, last, best, bestLap, led, incidents, outId, outText);

    private static SimSession With(params SimSessionResults[] results) => SimFixtures.Session() with { Results = results };

    private static SimSessionResults Race(int lapsComplete, bool official, params SimResultRow[] rows) =>
        new(2, official, lapsComplete, new SimFastestLap(2, 2, 59.2233), rows);

    [Fact]
    public void Converts_iRacings_values_into_the_contracts_terms()
    {
        var data = SessionResultsWatch.Convert(SimFixtures.Session(), Race(3, true,
            Row(2, 1, 0, 3, 0, 0, led: 1),
            Row(3, 2, 1, 2, 1, 62.682, last: -1, best: -1, bestLap: -1, incidents: 4),
            Row(1, 3, 2, 1, 2, 120.5, outId: 2, outText: "Disconnected")))!;

        Assert.Equal(new FastestLapData("33", 2, 59.2233), data.FastestLap);
        Assert.Equal(["33", "4", "12"], data.Cars.Select(c => c.Car));
        Assert.Equal([1, 2, 3], data.Cars.Select(c => c.ClassPosition));
        Assert.Equal(new ResultCarData("4", 2, 2, 2, 1, 62.682, null, null, null, 0, 4, null), data.Cars[1]);
        Assert.Equal(new ResultOutData(2, "Disconnected"), data.Cars[2].Out);
        var json = ContractJson.Serialize(new AgentEventMessage { AgentId = "agt_01", Seq = 913, EventTime = T, EventType = EventType.SessionResults, Data = data });
        ContractSchemas.AssertValid("agent-messages.schema.json", json);
        Assert.Contains("\"lastLap\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaves_out_placeholder_rows_before_the_first_lap_the_pace_car_and_unknown_cars()
    {
        Assert.Null(SessionResultsWatch.Convert(SimFixtures.Session(), Race(0, false, Row(1, 1, 0, 0, 0, 0.001))));

        var data = SessionResultsWatch.Convert(SimFixtures.Session(), Race(1, false, Row(0, 1, 0, 1, 0, 0), Row(9, 2, 1, 1, 0, 1.2), Row(1, 3, 2, 1, 0, 2.5)))!;
        Assert.Equal(["12"], data.Cars.Select(c => c.Car));
    }

    [Fact]
    public void Sends_a_session_again_only_when_its_results_changed()
    {
        var watch = new SessionResultsWatch();
        var lap1 = Race(1, false, Row(1, 1, 0, 1, 0, 0), Row(2, 2, 1, 1, 0, 0.85));

        Assert.Single(watch.Observe(With(lap1)));
        Assert.Empty(watch.Observe(With(lap1)));
        Assert.Single(watch.Observe(With(lap1 with { Official = true })));

        // Another session's results do not repeat the race's.
        var practice = new SimSessionResults(0, true, 5, null, [Row(3, 1, 0, 5, 0, 0)]);
        Assert.Equal([0], watch.Observe(With(practice, lap1 with { Official = true })).Select(r => r.SessionNum));
    }
}
