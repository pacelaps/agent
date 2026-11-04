using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.ConsoleHost.Spikes.Guided;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class GuidedStepFileTests
{
    [Theory]
    [InlineData("spike3-steps.json")]
    [InlineData("spike3-restart-recheck.json")]
    [InlineData("spike9-steps.json")]
    [InlineData("spike12-steps.json")]
    [InlineData("spike13-steps.json")]
    [InlineData("spike14-steps.json")]
    public void Every_shipped_steps_file_loads_by_bare_name(string file)
    {
        var plan = GuidedSteps.Load(file);

        Assert.NotEmpty(plan.Steps);
    }

    [Fact]
    public void Spike9_arms_its_one_to_go_steps_and_ends_watches_at_green()
    {
        var plan = GuidedSteps.Load("spike9-steps.json");

        Assert.Equal(3, plan.Steps.Count(s => s.SendWhen == "oneLapToGreen"));
        Assert.All(plan.Steps.Where(s => s.WatchOnly), s => Assert.Equal("green", s.EndWhen));
        Assert.DoesNotContain(plan.Steps, s => s.UsesCar);
    }

    [Fact]
    public void Spike14_needs_the_operators_car_and_has_final_questions()
    {
        var plan = GuidedSteps.Load("spike14-steps.json");

        Assert.Contains(plan.Steps, s => s.UsesCar);
        Assert.Equal(3, plan.FinalQuestions.Count);
    }

    [Fact]
    public void A_step_uses_the_car_through_a_placeholder_or_a_per_car_expectation()
    {
        Assert.True(new GuidedStep { Id = "a", Command = "!eol #{car}" }.UsesCar);
        Assert.True(new GuidedStep { Id = "b", Command = "!x", Expect = ["carBlack"] }.UsesCar);
        Assert.False(new GuidedStep { Id = "c", Command = "!yellow", Expect = ["caution"] }.UsesCar);
        Assert.True(new GuidedStep { Id = "d", Command = "!x", Target = "car2", Expect = ["carWavedAround"] }.UsesCar2);
    }

    [Theory]
    [InlineData("""{"steps":[{"id":"a","watchOnly":true,"command":"!x"}]}""", "watch-only but has a command")]
    [InlineData("""{"steps":[{"id":"a","command":"!x","sendWhen":"soon"}]}""", "unknown expectation 'soon'")]
    [InlineData("""{"steps":[{"id":"a","watchOnly":true,"endWhen":"later"}]}""", "unknown expectation 'later'")]
    public void Rejects_bad_watch_and_condition_settings(string json, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => GuidedSteps.Parse(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watch_only_step_needs_no_command()
    {
        var plan = GuidedSteps.Parse("""{"steps":[{"id":"w","watchOnly":true,"expect":["green"],"endWhen":"green"}],"finalQuestions":["ok?"]}""");

        Assert.True(plan.Steps[0].WatchOnly);
        Assert.Equal(["ok?"], plan.FinalQuestions);
    }
}

public sealed class CautionExpectationTests
{
    private static readonly ExpectationTargets Targets = new(null, 0);

    private static TelemetryFrame Flags(uint flags) => new(1, 0, new Dictionary<string, object[]> { ["SessionFlags"] = [flags] });

    [Fact]
    public void One_lap_to_green_and_extended_caution()
    {
        const uint caution = SdkNames.FlagCaution;
        const uint oneToGo = SdkNames.FlagOneLapToGreen;

        Assert.True(Expectations.Holds("oneLapToGreen", Flags(caution | oneToGo), Targets));
        Assert.False(Expectations.Holds("cautionExtended", Flags(caution | oneToGo), Targets));
        Assert.True(Expectations.Holds("cautionExtended", Flags(caution), Targets));

        // At the green flag one-to-go also clears, but that is not an extension.
        Assert.False(Expectations.Holds("cautionExtended", Flags(SdkNames.FlagGreen | oneToGo), Targets));
        Assert.False(Expectations.Holds("cautionExtended", Flags(0x10000000), Targets));
    }
}

public sealed class Spike4IncidentTests
{
    private static IncidentObservation O(string source, int old, int @new, double seconds, int car = 5) =>
        new(source, car, old, @new, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Matches_each_telemetry_change_to_the_first_session_info_change_that_reaches_it()
    {
        var observations = new[]
        {
            O("telemetry", 0, 1, 10.0),
            O("driverIncidentCount", 0, 1, 10.4),
            O("driverInfoCar", 0, 1, 10.6),
            O("resultsPositions", 0, 1, 11.5),
            O("telemetry", 1, 5, 50.0),
            O("driverInfoCar", 1, 5, 50.6),
            O("resultsPositions", 1, 5, 51.2),
            O("resultsPositions", 0, 3, 51.0, car: 9),
        };

        var latencies = Spike4Incidents.Match(observations, ownCarIdx: 5);

        Assert.Equal(2, latencies.Count);
        Assert.Equal(1.5, latencies[0].LatencySecondsBySource["resultsPositions"]);
        Assert.Equal(0.4, latencies[0].LatencySecondsBySource["driverIncidentCount"]);
        Assert.Equal(0.6, latencies[0].LatencySecondsBySource["driverInfoCar"]);
        Assert.Equal(1.2, latencies[1].LatencySecondsBySource["resultsPositions"]);
        Assert.Null(latencies[1].LatencySecondsBySource["driverIncidentCount"]);
        Assert.Equal("pass", Spike4Incidents.Evaluate(latencies));
    }

    [Fact]
    public void Fails_when_others_see_it_late_or_never_and_is_invalid_without_incidents()
    {
        var late = Spike4Incidents.Match([O("telemetry", 0, 1, 10), O("driverInfoCar", 0, 1, 13)], 5);
        var never = Spike4Incidents.Match([O("telemetry", 0, 1, 10), O("resultsPositions", 0, 1, 10.5)], 5);

        Assert.Equal("fail", Spike4Incidents.Evaluate(late));
        Assert.Equal("fail", Spike4Incidents.Evaluate(never));
        Assert.StartsWith("invalid", Spike4Incidents.Evaluate([]), StringComparison.Ordinal);
    }

    [Fact]
    public void Collects_counts_from_all_session_info_sources()
    {
        var s = new SessionInfoView
        {
            UpdateCount = 1, BuildVersion = null, SimMode = null, LeagueId = 0, SeasonId = 0, SessionId = 0, SubSessionId = 0,
            CurrentSessionNum = 0, Sessions = [], DriverCarIdx = 2, DriverIsAdmin = false, PaceCarIdx = 0,
            Cars = [new CarEntry(0, "0", 1, true, false, 0), new CarEntry(2, "12", 1, false, false, 0, CurDriverIncidentCount: 4)],
            DriverIncidentCount = 4,
            ResultIncidents = new Dictionary<int, int> { [2] = 3 },
        };

        var counts = Spike4Incidents.SessionCounts(s);

        Assert.Equal(3, counts[("resultsPositions", 2)]);
        Assert.Equal(4, counts[("driverInfoCar", 2)]);
        Assert.Equal(4, counts[("driverIncidentCount", 2)]);
        Assert.False(counts.ContainsKey(("driverInfoCar", 0)));
    }
}

public sealed class Spike5FieldTests
{
    private static TelemetryFrame F(double seconds, float dist, int surface = SdkNames.SurfaceOnTrack) =>
        new(0, seconds, new Dictionary<string, object[]>
        {
            ["CarIdxLapDistPct"] = [dist],
            ["CarIdxTrackSurface"] = [surface],
        }, TimeSpan.FromSeconds(seconds));

    [Fact]
    public void Counts_changes_and_the_longest_gap_while_on_track()
    {
        var stats = new FieldUpdateStats(["CarIdxLapDistPct"]);
        stats.Observe(F(0.0, 0.10f), _ => true);
        stats.Observe(F(0.5, 0.11f), _ => true);
        stats.Observe(F(1.0, 0.11f), _ => true);
        stats.Observe(F(2.2, 0.12f), _ => true);

        var r = Assert.Single(stats.Results());
        Assert.Equal(2, r.Changes);
        Assert.Equal(1.7, r.MaxGapSeconds);
        Assert.Equal(4, stats.Frames);
        Assert.Equal("fail", Spike5Fields.Evaluate(stats.Results(), [], stats.Frames).Result);
    }

    [Fact]
    public void A_car_parked_in_the_pit_stall_does_not_count_as_a_gap()
    {
        var stats = new FieldUpdateStats(["CarIdxLapDistPct"]);
        stats.Observe(F(0.0, 0.10f), _ => true);
        stats.Observe(F(0.5, 0.11f), _ => true);
        stats.Observe(F(1.0, 0.11f, surface: 1), _ => true);
        stats.Observe(F(9.0, 0.11f, surface: 1), _ => true);
        stats.Observe(F(9.5, 0.12f), _ => true);

        var r = Assert.Single(stats.Results());
        Assert.Equal(0.5, r.MaxGapSeconds);
        Assert.Equal("pass", Spike5Fields.Evaluate(stats.Results(), [], stats.Frames).Result);
    }

    [Fact]
    public void Missing_fields_fail_the_spike()
    {
        var s = Spike5Fields.Evaluate([new FieldUpdate("CarIdxLapDistPct", 0, 10, 0.1)], ["CarIdxClassPosition"], 100);

        Assert.Equal("fail", s.Result);
        Assert.Contains(s.Reasons, r => r.Contains("CarIdxClassPosition", StringComparison.Ordinal));
    }
}

public sealed class Spike6ReplayTests
{
    private static Spike6Replay.ReplaySample S(int? end, double sessionTime, int? laps = null) => new(end > 0, 100, end, 1, 50, sessionTime, 0.5, laps, 20);

    [Fact]
    public void Reads_a_jump_back_to_live_with_telemetry_staying_live()
    {
        var text = Spike6Replay.Assess(S(0, 100), S(3600, 108), S(0, 116));

        Assert.Contains("in replay (3600 frames behind live)", text, StringComparison.Ordinal);
        Assert.Contains("back at live end", text, StringComparison.Ordinal);
        Assert.Contains("SessionTime kept running", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Car_laps_tell_whether_per_car_telemetry_follows_the_replay()
    {
        Assert.Contains("per-car telemetry stays live", Spike6Replay.Assess(S(0, 100, 300), S(3600, 108, 302), S(0, 116, 303)), StringComparison.Ordinal);
        Assert.Contains("per-car telemetry follows the replay", Spike6Replay.Assess(S(0, 100, 300), S(3600, 108, 280), S(0, 116, 303)), StringComparison.Ordinal);
    }

    [Fact]
    public void Flags_telemetry_that_follows_the_replay()
    {
        var text = Spike6Replay.Assess(S(0, 100), S(3600, 40), S(60, 116));

        Assert.Contains("telemetry follows the replay", text, StringComparison.Ordinal);
        Assert.Contains("still 60 frames behind live", text, StringComparison.Ordinal);
    }
}

public sealed class Spike7RulerTests
{
    [Theory]
    [InlineData("/rc", 60)]
    [InlineData("/rc", 255)]
    [InlineData("", 100)]
    [InlineData("", 13)]
    [InlineData("/longprefix", 40)]
    public void Ruler_has_the_exact_length_and_every_number_ends_at_its_position(string prefix, int length)
    {
        var text = Spike7Length.Ruler(prefix, length);

        Assert.Equal(length, text.Length);
        if (prefix.Length > 0)
        {
            Assert.StartsWith(prefix + " ", text, StringComparison.Ordinal);
        }

        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, "[0-9]+"))
        {
            Assert.Equal(int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture), m.Index + m.Length);
        }
    }

    [Fact]
    public void Plain_ruler_starts_like_this()
    {
        Assert.Equal("........10........20", Spike7Length.Ruler("", 20));
        Assert.Equal("/rc ....10........20.", Spike7Length.Ruler("/rc", 21));
    }

    [Fact]
    public void Estimates_the_limit_from_cut_messages()
    {
        Assert.Null(Spike7Length.EstimateLimit([(60, 60), (100, 100)]));
        Assert.Equal(200, Spike7Length.EstimateLimit([(100, 100), (255, 200), (300, 200), (400, 0)]));
    }
}

public sealed class Spike8And11Tests
{
    [Theory]
    [InlineData(true, false, false, "pass")]
    [InlineData(true, true, true, "pass")]
    [InlineData(true, true, false, "fail (DriverIsAdmin disagrees)")]
    [InlineData(false, true, true, "fail (name not readable)")]
    [InlineData(true, false, null, "partial (admin state unknown)")]
    public void Spike8(bool nameReadable, bool driverIsAdmin, bool? operatorSays, string expected) =>
        Assert.Equal(expected, Spike8And11Identity.Evaluate8(nameReadable, driverIsAdmin, operatorSays));

    [Theory]
    [InlineData("o", 0, null, "not a league session (ids recorded, spike still open)")]
    [InlineData("l", 0, 1234, "fail (league id is 0 in a league session)")]
    [InlineData("l", 1234, 1234, "pass")]
    [InlineData("l", 1234, null, "partial (league id not compared)")]
    [InlineData("l", 1234, 99, "fail (league id differs from the league page)")]
    public void Spike11(string kind, int leagueId, int? page, string expected) =>
        Assert.Equal(expected, Spike8And11Identity.Evaluate11(kind, leagueId, page));
}

public sealed class Spike10MacroConfigTests
{
    [Fact]
    public void Finds_the_marker_with_its_section_and_only_the_key_names_of_that_section()
    {
        string[] lines =
        [
            "[Graphics]",
            "fullscreen=0",
            "[Chat Macros]",
            "macro1=Good luck everyone",
            "macro14=spike10markerA$",
            "[Other]",
            "x=1",
        ];

        var hit = Assert.Single(Spike10MacroConfig.FindInLines("app.ini", lines, "spike10markerA"));

        Assert.Equal(5, hit.Line);
        Assert.Equal("[Chat Macros]", hit.Section);
        Assert.Equal("macro14=spike10markerA$", hit.Text);
        Assert.Equal(["macro1", "macro14"], hit.SectionKeys);
    }

    [Fact]
    public void A_marker_outside_any_section_has_no_section()
    {
        var hit = Assert.Single(Spike10MacroConfig.FindInLines("x.txt", ["spike10markerB here"], "spike10markerB"));

        Assert.Null(hit.Section);
        Assert.Empty(hit.SectionKeys);
    }

    [Fact]
    public void Searches_a_folder_and_lists_binding_candidates()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spike10-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        try
        {
            File.WriteAllLines(Path.Combine(dir, "app.ini"), ["[Chat]", "m1=spike10markerC$"]);
            File.WriteAllText(Path.Combine(dir, "sub", "controls.cfg"), "binary-ish");
            File.WriteAllBytes(Path.Combine(dir, "big.bin"), new byte[10]);

            var hits = Spike10MacroConfig.Search(dir, "spike10markerC");
            var bindings = Spike10MacroConfig.BindingCandidates(dir);

            Assert.Equal("app.ini", Assert.Single(hits).File);
            Assert.Equal(Path.Combine("sub", "controls.cfg"), Assert.Single(bindings).File);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
