using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Events;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Snapshots;
using PaceLaps.Agent.IRacing.Sdk;
using PaceLaps.Agent.Tests.Contracts;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Snapshots;

public sealed class FlagNormalizerTests
{
    private const uint StartHidden = 0x10000000;

    [Fact]
    public void Green_is_derived_while_racing_without_caution()
    {
        // Spike 3: under green the sim mostly reports only startHidden.
        Assert.Equal(["green"], FlagNormalizer.Normalize(StartHidden, SimValues.StateRacing));
        Assert.Equal(["green"], FlagNormalizer.Normalize(SimValues.FlagGreen, SimValues.StateRacing));
    }

    [Fact]
    public void No_green_before_the_race_or_after_the_checkered_flag()
    {
        Assert.Empty(FlagNormalizer.Normalize(StartHidden, 3));
        Assert.Equal(["checkered"], FlagNormalizer.Normalize(SimValues.FlagCheckered, SimValues.StateRacing));
        Assert.Equal(["red"], FlagNormalizer.Normalize(SimValues.FlagRed, SimValues.StateRacing));
    }

    [Fact]
    public void Caution_covers_both_caution_bits_and_waving_is_added()
    {
        Assert.Equal(["caution", "caution_waving"], FlagNormalizer.Normalize(SimValues.FlagCautionWaving | StartHidden, SimValues.StateRacing));
        Assert.Equal(["caution"], FlagNormalizer.Normalize(SimValues.FlagCaution, SimValues.StateRacing));
        Assert.Equal(["caution", "one_lap_to_green", "green_held"],
            FlagNormalizer.Normalize(SimValues.FlagCaution | SimValues.FlagOneLapToGreen | SimValues.FlagGreenHeld, SimValues.StateRacing));
    }

    [Fact]
    public void Every_name_is_in_the_contract_vocabulary()
    {
        var all = FlagNormalizer.Normalize(0x0000FFFF, SimValues.StateRacing).Concat(FlagNormalizer.Normalize(0, SimValues.StateRacing));
        var vocabulary = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractSchemas.Folder, "common.schema.json")))
            .RootElement.GetProperty("$defs").GetProperty("flagName").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToHashSet();

        Assert.All(all, name => Assert.Contains(name, vocabulary));
    }
}

public sealed class SnapshotBuilderTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 7, 19, 3, 11, 250, TimeSpan.Zero);

    [Fact]
    public void Builds_a_valid_snapshot_with_all_cars()
    {
        var session = SimFixtures.Session();
        var frame = SimFixtures.Frame(cars:
        [
            (0, SimFixtures.Car(surface: 1, pos: 0, pit: true)),
            (1, SimFixtures.Car(lapCompleted: 14, dist: 0.431f, pos: 1)),
            (2, SimFixtures.Car(lapCompleted: 14, dist: 0.402f, pos: 2)),
        ]);

        var snapshot = SnapshotBuilder.Build("agt_01", 1042, T, session, frame);

        ContractSchemas.AssertValid("snapshot.schema.json", ContractJson.Serialize(snapshot));
        Assert.Equal(["green"], snapshot.Session.Flags);
        Assert.Equal("Racing", snapshot.Session.State);
        Assert.Equal(4, snapshot.Cars.Count);
        var pace = snapshot.Cars[0];
        Assert.True(pace.IsPaceCar);
        Assert.Equal("InPitStall", pace.Surface);
        Assert.Null(pace.CustId);
        var leader = snapshot.Cars.Single(c => c.Num == "12");
        Assert.Equal(14, leader.Lap);
        Assert.Equal(0.431, leader.LapDistPct, 3);
        Assert.Equal(100001, leader.CustId);
    }

    [Fact]
    public void The_layout_being_raced_goes_with_the_snapshot_for_race_controls_track_map()
    {
        var session = SimFixtures.Session() with
        {
            Track = SimTrack.From(572, "Autódromo Hermanos Rodríguez", "Grand Prix", "4.30 km"),
        };

        var snapshot = SnapshotBuilder.Build("a", 1, T, session, SimFixtures.Frame());
        var json = ContractJson.Serialize(snapshot);

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        var track = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("session").GetProperty("track");
        Assert.Equal(572, track.GetProperty("id").GetInt32());
        Assert.Equal("Autódromo Hermanos Rodríguez", track.GetProperty("name").GetString());
        Assert.Equal("Grand Prix", track.GetProperty("config").GetString());
        Assert.Equal(4300, track.GetProperty("lengthM").GetDouble());
    }

    [Fact]
    public void Lap_times_and_sector_starts_go_with_the_snapshot_for_race_controls_lap_times()
    {
        var session = SimFixtures.Session() with
        {
            Track = SimTrack.From(353, "Lime Rock Park", "Grand Prix", "2.46 km")! with { SectorStarts = [0, 0.230691, 0.478639, 0.627839] },
        };
        var frame = SimFixtures.Frame(cars: [(1, SimFixtures.Car() with { LastLapTime = 59.78634f, BestLapTime = 59.42581f }), (2, SimFixtures.Car())]);

        var json = ContractJson.Serialize(SnapshotBuilder.Build("a", 1, T, session, frame));

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.Equal([0, 0.23069, 0.47864, 0.62784], root.GetProperty("session").GetProperty("track").GetProperty("sectorStarts").EnumerateArray().Select(x => x.GetDouble()));
        var cars = root.GetProperty("cars").EnumerateArray().ToDictionary(c => c.GetProperty("num").GetString()!);
        Assert.Equal(59.7863, cars["12"].GetProperty("lastLapTime").GetDouble(), 4);
        Assert.Equal(59.4258, cars["12"].GetProperty("bestLapTime").GetDouble(), 4);
        Assert.False(cars["33"].TryGetProperty("lastLapTime", out _), "no time from iRacing (-1): left out");
    }

    [Fact]
    public void A_timed_session_and_the_weather_go_with_the_snapshot_for_race_controls_header_and_weather_card()
    {
        // As the session and weather check recorded them: a 600 s session (unlimited laps 32767), rain coming.
        var frame = SimFixtures.Frame() with
        {
            Length = new SimSessionLength(32767, 32767, 600, 534.15),
            Weather = new SimWeather(19.971f, 19.444f, 4, 0.006f, true, 2, 1.881f, 0.913f, 0.859f, 0f),
        };

        var json = ContractJson.Serialize(SnapshotBuilder.Build("a", 1, T, SimFixtures.Session(), frame));

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        var session = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("session");
        Assert.False(session.TryGetProperty("laps", out _), "unlimited laps are left out");
        Assert.Equal(600, session.GetProperty("time").GetProperty("totalSec").GetDouble());
        Assert.Equal(534.2, session.GetProperty("time").GetProperty("remainingSec").GetDouble());
        var weather = session.GetProperty("weather");
        Assert.Equal(20.0, weather.GetProperty("airTempC").GetDouble());
        Assert.Equal(19.4, weather.GetProperty("trackTempC").GetDouble());
        Assert.Equal("lightly_wet", weather.GetProperty("wetness").GetString());
        Assert.Equal("mostly_cloudy", weather.GetProperty("skies").GetString());
        Assert.Equal(0.6, weather.GetProperty("precipitationPct").GetDouble());
        Assert.True(weather.GetProperty("declaredWet").GetBoolean());
        Assert.Equal(52.3, weather.GetProperty("windDirDeg").GetDouble());
        Assert.Equal(85.9, weather.GetProperty("humidityPct").GetDouble());
    }

    [Fact]
    public void A_lap_race_sends_its_laps_and_no_time_and_an_unknown_wetness_is_left_out()
    {
        // The 5-lap race: a time total of a day (not a limit), the leader on lap 2 (4 remaining), wetness 0 at the start.
        var frame = SimFixtures.Frame() with
        {
            Length = new SimSessionLength(5, 4, 86400, 86263),
            Weather = new SimWeather(20.1f, 21.1f, 0, 0.82f, true, 3, 3.9f, (float)(2 * Math.PI - 0.0001), 0.95f, 0f),
        };

        var json = ContractJson.Serialize(SnapshotBuilder.Build("a", 1, T, SimFixtures.Session(), frame));

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        var session = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("session");
        Assert.Equal(5, session.GetProperty("laps").GetProperty("total").GetInt32());
        Assert.Equal(4, session.GetProperty("laps").GetProperty("remaining").GetInt32());
        Assert.False(session.TryGetProperty("time", out _), "a day is no time limit");
        Assert.False(session.GetProperty("weather").TryGetProperty("wetness", out _));
        Assert.Equal(0, session.GetProperty("weather").GetProperty("windDirDeg").GetDouble());
    }

    [Fact]
    public void The_car_classes_go_with_the_snapshot_as_iRacing_reports_them()
    {
        // The class names check (2026-11-04): in an AI session the names are empty and the colours 0xrrggbb.
        var session = SimFixtures.Session() with
        {
            Cars =
            [
                new SimCar(0, "0", 11, IsPaceCar: true, IsSpectator: false, 0, null, "", "0xffffff", "Safety Car"),
                new SimCar(1, "218", 4111, false, false, 1, 0, "", "0xae6bff", "Mercedes AMG GT4"),
                new SimCar(2, "26", 4111, false, false, 2, 0, null, "0xae6bff", "Mercedes AMG GT4"),
                new SimCar(3, "7", 4011, false, false, 3, 0, "  GT3 ", "0x33ceff", "Ferrari 296 GT3"),
                new SimCar(4, "8", 4011, false, false, 4, 0, "GT3", "0x33ceff", "BMW M4 GT3 EVO"),
                new SimCar(5, "9", 74, false, false, 5, 0, null, null, null),
                new SimCar(6, "", 4111, false, IsSpectator: true, 6, null, "", "0x123456", "Spectator"),
            ],
        };

        var json = ContractJson.Serialize(SnapshotBuilder.Build("a", 1, T, session, SimFixtures.Frame()));

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        var classes = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("session").GetProperty("classes");
        Assert.Equal(
            """[{"id":74},{"id":4011,"name":"GT3","colour":"#33ceff","models":["Ferrari 296 GT3","BMW M4 GT3 EVO"]},{"id":4111,"colour":"#ae6bff","models":["Mercedes AMG GT4"]}]""",
            classes.GetRawText());
    }

    [Theory]
    [InlineData("0xffda59", "#ffda59")]
    [InlineData("0X0000FF", "#0000ff")]
    [InlineData("ffda59", "#ffda59")]
    [InlineData("0x0", null)]
    [InlineData("", null)]
    [InlineData("red", null)]
    [InlineData("0x1ffffff", null)]
    public void A_class_colour_is_read_as_iRacing_writes_it(string value, string? expected) =>
        Assert.Equal(expected, SnapshotBuilder.Colour(value));

    [Fact]
    public void Without_a_named_track_the_snapshot_leaves_it_out()
    {
        var json = ContractJson.Serialize(SnapshotBuilder.Build("a", 1, T, SimFixtures.Session(), SimFixtures.Frame()));

        ContractSchemas.AssertValid("snapshot.schema.json", json);
        Assert.DoesNotContain("\"track\"", json, StringComparison.Ordinal);
        Assert.Null(SimTrack.From(0, "Road America", "Full Course", "6.51 km"));
        Assert.Null(SimTrack.From(18, " ", "Full Course", "6.51 km"));
    }

    [Theory]
    [InlineData("4.30 km", 4300.0)]
    [InlineData("2.67 mi", 4296.9)]
    [InlineData("800 m", 800.0)]
    [InlineData(" 6.51 KM ", 6510.0)]
    [InlineData("", null)]
    [InlineData("unknown", null)]
    [InlineData("0 km", null)]
    public void Track_lengths_are_read_in_metres(string text, double? metres) =>
        Assert.Equal(metres, SimTrack.LengthInMetres(text));

    [Fact]
    public void Cars_not_in_the_world_are_disconnected_with_values_in_range()
    {
        var snapshot = SnapshotBuilder.Build("a", 1, T, SimFixtures.Session(), SimFixtures.Frame());
        var car4 = snapshot.Cars.Single(c => c.Num == "4");

        Assert.False(car4.Connected);
        Assert.Equal("NotInWorld", car4.Surface);
        Assert.Equal(0, car4.LapDistPct);
        Assert.Equal(0, car4.Lap);
        ContractSchemas.AssertValid("snapshot.schema.json", ContractJson.Serialize(snapshot));
    }

    [Fact]
    public void Spectators_are_left_out()
    {
        var session = SimFixtures.Session() with
        {
            Cars = [.. SimFixtures.Session().Cars, new SimCar(9, "", 0, false, true, 5, null)],
        };

        Assert.DoesNotContain(SnapshotBuilder.Build("a", 1, T, session, SimFixtures.Frame()).Cars, c => c.Num == "");
    }
}

public sealed class EventDetectorTests
{
    [Fact]
    public void First_observation_sets_the_baseline_only()
    {
        var detector = new EventDetector();

        Assert.Empty(detector.Observe(SimFixtures.Session(), SimFixtures.Frame()));
    }

    [Fact]
    public void Reports_a_flag_change_with_previous_flags()
    {
        var detector = new EventDetector();
        detector.Observe(SimFixtures.Session(), SimFixtures.Frame());

        var e = Assert.Single(detector.Observe(SimFixtures.Session(), SimFixtures.Frame(flags: SimValues.FlagCautionWaving, time: 900.5)));

        Assert.Equal(EventType.FlagChange, e.Type);
        var data = Assert.IsType<FlagChangeData>(e.Data);
        Assert.Equal(["caution", "caution_waving"], data.Flags);
        Assert.Equal(["green"], data.Previous);
        Assert.Equal(900.5, data.SessionTimeSec);
        Assert.Empty(detector.Observe(SimFixtures.Session(), SimFixtures.Frame(flags: SimValues.FlagCautionWaving)));
    }

    [Fact]
    public void Reports_a_session_change()
    {
        var detector = new EventDetector();
        detector.Observe(SimFixtures.Session(sessionNum: 1, type: "Qualify"), SimFixtures.Frame(sessionNum: 1));

        var events = detector.Observe(SimFixtures.Session(sessionNum: 2), SimFixtures.Frame(sessionNum: 2));

        var data = Assert.IsType<SessionChangeData>(Assert.Single(events, e => e.Type == EventType.SessionChange).Data);
        Assert.Equal(new SessionChangeData(12345, 6789, 111, 222, 2, "Race"), data);
    }

    [Fact]
    public void Reports_incident_deltas_with_position_on_track()
    {
        var detector = new EventDetector();
        detector.Observe(SimFixtures.Session(cars: [(1, "12", 4), (2, "33", 0)]), SimFixtures.Frame());

        var frame = SimFixtures.Frame(time: 863.2, cars: [(1, SimFixtures.Car(lapCompleted: 15, dist: 0.62f))]);
        var events = detector.Observe(SimFixtures.Session(updateCount: 2, cars: [(1, "12", 6), (2, "33", 0)]), frame);

        var data = Assert.IsType<IncidentData>(Assert.Single(events).Data);
        Assert.Equal(new IncidentData("12", 2, 6, 2, 863.2, 15, 0.62), data);
    }

    [Fact]
    public void A_reset_incident_count_is_not_an_event()
    {
        var detector = new EventDetector();
        detector.Observe(SimFixtures.Session(cars: [(1, "12", 6)]), SimFixtures.Frame());

        Assert.Empty(detector.Observe(SimFixtures.Session(cars: [(1, "12", 0)]), SimFixtures.Frame()));
        Assert.Single(detector.Observe(SimFixtures.Session(cars: [(1, "12", 1)]), SimFixtures.Frame()));
    }

    /// <summary>The session with car 12 driven by the given customer id (a team race's drivers), car 33 unchanged.</summary>
    private static SimSession Driving(int custId, int incidents12, int incidents33 = 0)
    {
        var session = SimFixtures.Session(cars: [(1, "12", incidents12), (2, "33", incidents33)]);
        return session with { Cars = session.Cars.Select(c => c.Number == "12" ? c with { CustId = custId } : c).ToList() };
    }

    [Fact]
    public void A_driver_swap_starts_a_new_count_instead_of_a_false_incident()
    {
        var detector = new EventDetector();
        detector.Observe(Driving(custId: 200001, incidents12: 4), SimFixtures.Frame());

        // Driver B comes back in with 6x from an earlier stint: no incident, the new baseline is 6.
        Assert.Empty(detector.Observe(Driving(custId: 200002, incidents12: 6), SimFixtures.Frame()));
        // B's next incident counts from there.
        var data = Assert.IsType<IncidentData>(Assert.Single(detector.Observe(Driving(custId: 200002, incidents12: 8), SimFixtures.Frame())).Data);
        Assert.Equal(("12", 2, 8), (data.Car, data.Delta, data.Total));
        // Back to A with 4x: lower, a new baseline, no event; then A's 1x.
        Assert.Empty(detector.Observe(Driving(custId: 200001, incidents12: 4), SimFixtures.Frame()));
        Assert.Equal(1, Assert.IsType<IncidentData>(Assert.Single(detector.Observe(Driving(custId: 200001, incidents12: 5), SimFixtures.Frame())).Data).Delta);
    }

    [Fact]
    public void A_swap_in_one_car_does_not_hide_an_incident_in_another()
    {
        var detector = new EventDetector();
        detector.Observe(Driving(custId: 200001, incidents12: 4, incidents33: 0), SimFixtures.Frame());

        var e = Assert.Single(detector.Observe(Driving(custId: 200002, incidents12: 6, incidents33: 2), SimFixtures.Frame()));

        Assert.Equal("33", Assert.IsType<IncidentData>(e.Data).Car);
    }
}

public sealed class EventBufferTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 7, 19, 0, 0, TimeSpan.Zero);
    private static readonly DetectedEvent E = new(EventType.FlagChange, new FlagChangeData(["green"], [], 1, 1));

    [Fact]
    public void Assigns_increasing_sequence_numbers_and_replays_after_the_acknowledged_one()
    {
        var buffer = new EventBuffer();
        var seqs = Enumerable.Range(0, 5).Select(_ => buffer.Add("a", T, E).Seq).ToList();

        Assert.Equal([1L, 2, 3, 4, 5], seqs);
        buffer.Acknowledge(2);
        Assert.Equal([3L, 4, 5], buffer.After(buffer.LastAcked).Select(e => e.Seq));
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Drops_the_oldest_when_full_and_counts_them()
    {
        var buffer = new EventBuffer(capacity: 3);
        for (var i = 0; i < 5; i++)
        {
            buffer.Add("a", T, E);
        }

        Assert.Equal(2, buffer.Dropped);
        Assert.Equal([3L, 4, 5], buffer.After(0).Select(e => e.Seq));
    }

    [Fact]
    public void Acknowledging_an_older_number_keeps_the_newer_acknowledgement()
    {
        var buffer = new EventBuffer();
        buffer.Add("a", T, E);
        buffer.Add("a", T, E);
        buffer.Acknowledge(2);
        buffer.Acknowledge(1);

        Assert.Equal(2, buffer.LastAcked);
        Assert.Empty(buffer.After(2));
    }
}

public sealed class SimDataAdapterTests
{
    [Fact]
    public void Maps_telemetry_arrays_by_car_index()
    {
        var values = new Dictionary<string, object[]>
        {
            ["SessionNum"] = [2],
            ["SessionState"] = [4],
            ["SessionFlags"] = [SimValues.FlagCaution],
            ["PaceMode"] = [2],
            ["CarIdxLapCompleted"] = Enumerable.Range(0, 64).Select(i => (object)i).ToArray(),
            ["CarIdxLapDistPct"] = Enumerable.Range(0, 64).Select(i => (object)(i / 100f)).ToArray(),
            ["CarIdxOnPitRoad"] = Enumerable.Range(0, 64).Select(i => (object)(i == 3)).ToArray(),
            ["CarIdxTrackSurface"] = Enumerable.Range(0, 64).Select(_ => (object)3).ToArray(),
            ["CarIdxSessionFlags"] = Enumerable.Range(0, 64).Select(i => (object)(i == 5 ? SimValues.FlagBlack : 0u)).ToArray(),
        };

        var frame = SimDataAdapter.Map(new TelemetryFrame(7, 812.4, values, TimeSpan.FromSeconds(3)));

        Assert.Equal(SimValues.FlagCaution, frame.SessionFlags);
        Assert.Equal(2, frame.PaceMode);
        Assert.Equal(10, frame.Cars[10].LapCompleted);
        Assert.Equal(0.1f, frame.Cars[10].LapDistPct);
        Assert.True(frame.Cars[3].OnPitRoad);
        Assert.Equal(SimValues.FlagBlack, frame.Cars[5].SessionFlags);
        Assert.Equal(0, frame.Cars[5].Position);
        Assert.Equal(TimeSpan.FromSeconds(3), frame.Captured);
    }

    [Fact]
    public void Maps_the_session_with_incidents_from_the_driver_list()
    {
        var view = new SessionInfoView
        {
            UpdateCount = 3, BuildVersion = "b", SimMode = "full", LeagueId = 1, SeasonId = 2, SessionId = 3, SubSessionId = 4,
            CurrentSessionNum = 1, Sessions = [(0, "Practice"), (1, "Race")], DriverCarIdx = 2, DriverIsAdmin = true, PaceCarIdx = 0,
            Cars = [new CarEntry(0, "0", 11, true, false, -1), new CarEntry(2, "12", 4098, false, false, 555, CurDriverIncidentCount: 3), new CarEntry(3, "4", 4098, false, false, 556, CurDriverIncidentCount: 1)],
            ResultIncidents = new Dictionary<int, int> { [2] = 2 },
            OperatorName = "Example Operator",
            OperatorCustId = 555,
        };

        var s = SimDataAdapter.Map(view);

        Assert.Equal("Race", s.SessionType);
        Assert.Equal(3, s.CarByNumber("12")!.Incidents);
        Assert.Equal(1, s.CarByNumber("4")!.Incidents);
        Assert.Null(s.CarByIdx(0)!.Incidents);
        Assert.Equal("Example Operator", s.OperatorName);
    }
}
