using PaceLaps.Agent.ConsoleHost.Spikes.Guided;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class GuidedStepsTests
{
    private static string DefaultStepsPath =>
        Path.Combine(AppContext.BaseDirectory, "Spikes", "spike3-steps.json");

    [Fact]
    public void The_shipped_steps_file_loads_and_covers_the_spike_3_commands()
    {
        var plan = GuidedSteps.Load(DefaultStepsPath);
        var commands = string.Join(" ", plan.Steps.Select(s => s.Command));

        foreach (var c in new[] { "!yellow", "!restart single", "!restart double", "!pacelaps 4", "!pacelaps +2",
                     "!waveby", "!pitclose", "!pitopen", "!black", "!clear", "!eol" })
        {
            Assert.Contains(c, commands, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_restart_recheck_file_loads()
    {
        var plan = GuidedSteps.Load(Path.Combine(AppContext.BaseDirectory, "Spikes", "spike3-restart-recheck.json"));

        Assert.Equal(["yellow", "pace_laps_set", "restart_double", "restart_single"], plan.Steps.Select(s => s.Id));
    }

    [Fact]
    public void Renders_car_placeholders()
    {
        var step = new GuidedStep { Id = "x", Command = "!waveby #{car2} then #{car}" };

        Assert.Equal("!waveby #33 then #12", step.Render("12", "33"));
        Assert.True(step.UsesCar2);
    }

    [Theory]
    [InlineData("""{"steps":[{"id":"a","command":"!x {driver}"}]}""", "placeholder")]
    [InlineData("""{"steps":[{"id":"a","command":"!x","expect":["nope"]}]}""", "unknown expectation")]
    [InlineData("""{"steps":[{"id":"a","command":"!x"},{"id":"a","command":"!y"}]}""", "used twice")]
    [InlineData("""{"steps":[{"id":"a","command":" "}]}""", "no command")]
    [InlineData("""{"steps":[{"id":"a","command":"!x","target":"car3"}]}""", "target")]
    [InlineData("""{"steps":[{"id":"a","command":"!x","watchSeconds":0}]}""", "watchSeconds")]
    [InlineData("""{"about":"no steps"}""", "no \"steps\"")]
    public void Rejects_invalid_steps(string json, string message)
    {
        var e = Assert.Throws<InvalidDataException>(() => GuidedSteps.Parse(json));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("007", true)]
    [InlineData("999", true)]
    [InlineData("1000", false)]
    [InlineData("12a", false)]
    [InlineData("#12", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Validates_car_numbers(string? number, bool valid) =>
        Assert.Equal(valid, GuidedSteps.IsValidCarNumber(number));
}

public sealed class ExpectationsTests
{
    private const int Pace = 0;
    private const int Leader = 1;
    private const int Target = 2;
    private static readonly ExpectationTargets Targets = new(Target, Pace);

    private static TelemetryFrame Frame(int tick = 1, uint flags = SdkNames.FlagGreen, uint targetFlags = 0, uint targetPace = 0,
        int paceMode = 4, object? pitsOpen = null, float paceDist = 0.50f, float leaderDist = 0.48f, int paceSurface = SdkNames.SurfaceOnTrack)
    {
        var values = new Dictionary<string, object[]>
        {
            ["SessionFlags"] = [flags],
            ["PaceMode"] = [paceMode],
            ["CarIdxSessionFlags"] = [0u, 0u, targetFlags],
            ["CarIdxPaceFlags"] = [0u, 0u, targetPace],
            ["CarIdxPosition"] = [0, 1, 2],
            ["CarIdxLapDistPct"] = [paceDist, leaderDist, 0.3f],
            ["CarIdxTrackSurface"] = [paceSurface, SdkNames.SurfaceOnTrack, SdkNames.SurfaceOnTrack],
        };
        if (pitsOpen is not null)
        {
            values["PitsOpen"] = [pitsOpen];
        }

        return new TelemetryFrame(tick, tick / 60.0, values);
    }

    [Fact]
    public void Caution_and_green()
    {
        Assert.False(Expectations.Holds("caution", Frame(), Targets));
        Assert.True(Expectations.Holds("caution", Frame(flags: SdkNames.FlagCaution), Targets));
        Assert.True(Expectations.Holds("caution", Frame(flags: SdkNames.FlagCautionWaving), Targets));
        Assert.True(Expectations.Holds("green", Frame(), Targets));
        Assert.False(Expectations.Holds("green", Frame(flags: SdkNames.FlagGreen | SdkNames.FlagCaution), Targets));

        // Under green the sim mostly reports only startHidden (0x10000000), without the green bit.
        Assert.True(Expectations.Holds("green", Frame(flags: 0x10000000), Targets));
        Assert.False(Expectations.Holds("green", Frame(flags: SdkNames.FlagCheckered), Targets));
    }

    [Fact]
    public void Per_car_flags_use_the_target_car()
    {
        Assert.True(Expectations.Holds("carBlack", Frame(targetFlags: SdkNames.FlagBlack), Targets));
        Assert.False(Expectations.Holds("carBlackCleared", Frame(targetFlags: SdkNames.FlagBlack), Targets));
        Assert.True(Expectations.Holds("carWavedAround", Frame(targetPace: SdkNames.PaceWavedAround), Targets));
        Assert.True(Expectations.Holds("carEndOfLine", Frame(targetPace: SdkNames.PaceEndOfLine), Targets));
        Assert.Null(Expectations.Holds("carBlack", Frame(), new ExpectationTargets(null, Pace)));
    }

    [Fact]
    public void Pace_mode_and_pits()
    {
        Assert.True(Expectations.Holds("paceModeDouble", Frame(paceMode: SdkNames.PaceModeDoubleFileRestart), Targets));
        Assert.True(Expectations.Holds("paceModeSingle", Frame(paceMode: SdkNames.PaceModeSingleFileRestart), Targets));
        Assert.Null(Expectations.Holds("pitsClosed", Frame(), Targets));
        Assert.True(Expectations.Holds("pitsClosed", Frame(pitsOpen: false), Targets));
        Assert.True(Expectations.Holds("pitsOpen", Frame(pitsOpen: true), Targets));
    }

    [Fact]
    public void Pace_car_ahead_of_leader_handles_the_start_finish_wrap()
    {
        Assert.True(Expectations.Holds("paceCarAheadOfLeader", Frame(paceDist: 0.50f, leaderDist: 0.48f), Targets));
        Assert.True(Expectations.Holds("paceCarAheadOfLeader", Frame(paceDist: 0.01f, leaderDist: 0.98f), Targets));
        Assert.False(Expectations.Holds("paceCarAheadOfLeader", Frame(paceDist: 0.30f, leaderDist: 0.60f), Targets));
        Assert.Null(Expectations.Holds("paceCarAheadOfLeader", Frame(paceSurface: 1), Targets));
    }

    [Fact]
    public void Tracker_reports_verified_with_time_already_true_and_not_observed()
    {
        var verified = new ExpectationTracker("caution", Frame(), Targets);
        Assert.False(verified.Observe(Frame(tick: 2), TimeSpan.FromSeconds(0.5)));
        Assert.True(verified.Observe(Frame(tick: 3, flags: SdkNames.FlagCaution), TimeSpan.FromSeconds(1.25)));
        Assert.False(verified.Observe(Frame(tick: 4, flags: SdkNames.FlagCaution), TimeSpan.FromSeconds(2)));
        Assert.Equal(ExpectationOutcome.Verified, verified.Outcome);
        Assert.Equal(1.25, verified.FirstSeenAfterSeconds);

        var already = new ExpectationTracker("caution", Frame(flags: SdkNames.FlagCaution), Targets);
        already.Observe(Frame(tick: 2, flags: SdkNames.FlagCaution), TimeSpan.FromSeconds(1));
        Assert.Equal(ExpectationOutcome.AlreadyTrue, already.Outcome);

        var never = new ExpectationTracker("caution", Frame(), Targets);
        never.Observe(Frame(tick: 2), TimeSpan.FromSeconds(1));
        Assert.Equal(ExpectationOutcome.NotObserved, never.Outcome);

        var unavailable = new ExpectationTracker("pitsClosed", Frame(), Targets);
        unavailable.Observe(Frame(tick: 2), TimeSpan.FromSeconds(1));
        Assert.Equal(ExpectationOutcome.Unavailable, unavailable.Outcome);
    }

    [Fact]
    public void Every_known_expectation_can_be_evaluated()
    {
        foreach (var kind in Expectations.Known)
        {
            _ = Expectations.Holds(kind, Frame(pitsOpen: true), Targets);
        }
    }
}

public sealed class TelemetryDiffTests
{
    [Fact]
    public void Reports_scalar_and_per_car_changes_and_filters_unused_car_slots()
    {
        var before = new TelemetryFrame(1, 0, new Dictionary<string, object[]>
        {
            ["SessionFlags"] = [SdkNames.FlagGreen],
            ["CarIdxPaceFlags"] = [0u, 0u, 0u],
        });
        var after = new TelemetryFrame(2, 0, new Dictionary<string, object[]>
        {
            ["SessionFlags"] = [SdkNames.FlagCaution],
            ["CarIdxPaceFlags"] = [0u, SdkNames.PaceEndOfLine, 1u],
            ["PaceMode"] = [3],
        });

        var changes = TelemetryDiff.Diff(before, after, ["SessionFlags", "CarIdxPaceFlags", "PaceMode", "Missing"], idx => idx < 2).ToList();

        Assert.Equal(
        [
            new TelemetryChange("SessionFlags", 0, SdkNames.FlagGreen, SdkNames.FlagCaution),
            new TelemetryChange("CarIdxPaceFlags", 1, 0u, SdkNames.PaceEndOfLine),
            new TelemetryChange("PaceMode", 0, null, 3),
        ], changes);
    }

    [Theory]
    [InlineData("SessionFlags", 0x4004u, "green|caution")]
    [InlineData("CarIdxPaceFlags", 0x5u, "endOfLine|wavedAround")]
    [InlineData("PaceMode", 3, "doubleFileRestart")]
    [InlineData("CarIdxTrackSurface", 1, "inPitStall")]
    [InlineData("SessionState", 4, "racing")]
    [InlineData("CarIdxLapCompleted", 12, "12")]
    public void Describes_values(string var, object value, string expected) =>
        Assert.Equal(expected, TelemetryDiff.Describe(var, value));

    [Fact]
    public void Describes_unknown_flag_bits_in_hex()
    {
        Assert.Equal("none", SdkNames.SessionFlags(0));
        Assert.Equal("green|0x4000000", SdkNames.SessionFlags(0x04000004));
    }
}
