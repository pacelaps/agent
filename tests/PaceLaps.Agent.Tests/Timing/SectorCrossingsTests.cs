using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Timing;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.Timing;

public sealed class SectorCrossingsTests
{
    // Four sectors; car 12 at index 1, car 33 at index 2, the pace car at index 0.
    private static readonly SimSession Session = SimFixtures.Session() with
    {
        Track = SimTrack.From(353, "Lime Rock Park", "Grand Prix", "2.46 km")! with { SectorStarts = [0, 0.25, 0.5, 0.75] },
    };

    private static SimFrame At(double time, float pct, int lap = 5, bool pit = false, int surface = SimValues.SurfaceOnTrack, int sessionNum = 2,
        float pace = 0.1f) =>
        SimFixtures.Frame(time: time, sessionNum: sessionNum, cars: [(0, SimFixtures.Car(dist: pace)), (1, SimFixtures.Car(lap, pct, surface, pit: pit))]);

    [Fact]
    public void A_crossing_is_timed_between_two_frames()
    {
        var crossings = new SectorCrossings();
        Assert.Empty(crossings.Observe(Session, At(100.000, 0.24f)));

        var found = crossings.Observe(Session, At(100.100, 0.26f));

        var c = Assert.Single(found);
        Assert.Equal(new SectorCrossingData("12", 1, 5, 100.05), c);
    }

    [Fact]
    public void Crossing_the_line_names_the_lap_completed_after_it()
    {
        var crossings = new SectorCrossings();
        crossings.Observe(Session, At(10.0, 0.98f, lap: 5));

        var c = Assert.Single(crossings.Observe(Session, At(10.04, 0.02f, lap: 6)));

        Assert.Equal(0, c.Sector);
        Assert.Equal(6, c.Lap);
        Assert.Equal(10.02, c.SessionTimeSec, 3);
    }

    [Fact]
    public void Several_sector_starts_in_one_step_come_in_order()
    {
        // Two sector starts close together (some layouts have many sectors).
        var close = Session with { Track = Session.Track! with { SectorStarts = [0, 0.30, 0.32] } };
        var crossings = new SectorCrossings();
        crossings.Observe(close, At(1.0, 0.29f));

        var found = crossings.Observe(close, At(1.1, 0.33f));

        Assert.Equal([1, 2], found.Select(c => c.Sector));
        Assert.True(found[0].SessionTimeSec < found[1].SessionTimeSec);
    }

    [Fact]
    public void Pit_road_and_off_track_are_reported_with_the_next_crossing_only()
    {
        var crossings = new SectorCrossings();
        crossings.Observe(Session, At(1.0, 0.30f));
        crossings.Observe(Session, At(1.1, 0.35f, pit: true));
        crossings.Observe(Session, At(1.2, 0.40f, surface: SimValues.SurfaceOffTrack));
        var first = Assert.Single(crossings.Observe(Session, At(1.3, 0.51f)));
        crossings.Observe(Session, At(1.4, 0.60f));
        var second = Assert.Single(crossings.Observe(Session, At(1.5, 0.76f)));

        Assert.Equal((true, true, (bool?)null), (first.Pit, first.Off, first.Jump));
        Assert.Equal(((bool?)null, (bool?)null, (bool?)null), (second.Pit, second.Off, second.Jump));
    }

    [Fact]
    public void A_jump_gives_no_crossing_and_marks_the_next_one()
    {
        var crossings = new SectorCrossings();
        crossings.Observe(Session, At(1.0, 0.10f));

        Assert.Empty(crossings.Observe(Session, At(1.1, 0.60f))); // a tow or reset: half a lap in one frame
        var next = Assert.Single(crossings.Observe(Session, At(1.2, 0.76f)));

        Assert.Equal(3, next.Sector);
        Assert.True(next.Jump);
    }

    [Fact]
    public void A_car_out_of_the_world_and_back_and_a_gap_in_the_data_are_jumps()
    {
        var crossings = new SectorCrossings();
        crossings.Observe(Session, At(1.0, 0.20f));
        crossings.Observe(Session, At(1.1, -1f, surface: SimValues.SurfaceNotInWorld));
        crossings.Observe(Session, At(5.0, 0.20f));
        Assert.True(Assert.Single(crossings.Observe(Session, At(5.1, 0.26f))).Jump);

        crossings.Observe(Session, At(6.0, 0.60f));
        Assert.Empty(crossings.Observe(Session, At(7.0, 0.70f))); // a second without data
        Assert.True(Assert.Single(crossings.Observe(Session, At(7.1, 0.76f))).Jump);
    }

    [Fact]
    public void The_pace_car_layouts_without_sectors_and_a_new_session_give_nothing()
    {
        var crossings = new SectorCrossings();
        crossings.Observe(Session, At(1.0, 0.20f, pace: 0.24f));
        Assert.Empty(crossings.Observe(Session, At(1.1, 0.22f, pace: 0.26f)));

        var noSectors = Session with { Track = Session.Track! with { SectorStarts = [] } };
        Assert.Empty(new SectorCrossings().Observe(noSectors, At(1.0, 0.24f)));

        crossings.Observe(Session, At(2.0, 0.24f));
        Assert.Empty(crossings.Observe(Session, At(0.1, 0.40f, sessionNum: 3))); // the new session's first reading
        Assert.Single(crossings.Observe(Session, At(0.2, 0.51f, sessionNum: 3)));
    }
}
