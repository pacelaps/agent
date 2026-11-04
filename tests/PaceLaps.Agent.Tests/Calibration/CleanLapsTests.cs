using PaceLaps.Agent.Core.Calibration;

namespace PaceLaps.Agent.Tests.Calibration;

public sealed class CleanLapsTests
{
    [Fact]
    public void The_partial_lap_at_the_start_does_not_count_the_next_ones_do()
    {
        var laps = new CleanLaps();
        laps.Observe("12", 3, false, true); // somewhere in lap 4 when recording starts
        laps.Observe("12", 4, false, true); // lap 5 begins: the first clean lap
        Assert.Empty(laps.Done);
        laps.Observe("12", 5, false, true);
        laps.Observe("12", 6, false, true);

        Assert.Equal([4, 5], laps.Done["12"]);
        Assert.True(laps.Finished(2));
        Assert.False(laps.Finished(3));
    }

    [Fact]
    public void Each_clean_lap_is_reported_when_it_is_completed()
    {
        var laps = new CleanLaps();
        Assert.Null(laps.Observe("12", 3, false, true));
        Assert.Null(laps.Observe("12", 4, false, true)); // the partial lap ended: not clean
        Assert.Null(laps.Observe("12", 4, false, true));
        Assert.Equal(4, laps.Observe("12", 5, false, true));
        Assert.Null(laps.Observe("12", 5, true, true)); // pit road
        Assert.Null(laps.Observe("12", 6, false, true)); // back out: the lap it is in does not count
        Assert.Null(laps.Observe("12", 7, false, true));
        Assert.Equal(7, laps.Observe("12", 8, false, true));
    }

    [Fact]
    public void A_car_on_pit_road_is_not_waited_for_and_counts_again_from_its_next_full_lap()
    {
        var laps = new CleanLaps();
        laps.Observe("1", 0, false, true);
        laps.Observe("2", 0, false, true);
        laps.Observe("1", 1, false, true);
        laps.Observe("2", 1, false, true);
        laps.Observe("1", 2, false, true);
        laps.Observe("2", 1, true, true); // car 2 pits during its first clean lap
        Assert.True(laps.Finished(1), "car 2 on pit road is not waited for");

        laps.Observe("2", 2, false, true); // back out: the lap it is in does not count
        Assert.False(laps.Finished(1), "car 2 is back on track");
        laps.Observe("2", 3, false, true);
        laps.Observe("2", 4, false, true);

        Assert.Equal([1], laps.Done["1"]);
        Assert.Equal([3], laps.Done["2"]);
        Assert.True(laps.Finished(1));
    }

    [Fact]
    public void A_car_towed_after_a_crash_counts_again_once_it_drives_on()
    {
        // As at Lime Rock: the only car crashed, was towed (out of the world, then in its stall) and drove on.
        var laps = new CleanLaps();
        laps.Observe("1", 0, false, true);
        laps.Observe("1", 1, false, true);
        laps.Observe("1", 2, false, true); // lap 1 clean
        laps.Observe("1", 2, false, false); // towed
        laps.Observe("1", 2, true, true); // in the pit stall
        laps.Observe("1", 2, false, true); // out again: lap 3 has begun on pit road
        laps.Observe("1", 3, false, true);
        laps.Observe("1", 4, false, true);

        Assert.Equal([1, 3], laps.Done["1"]);
        Assert.True(laps.Finished(2));
    }

    [Fact]
    public void A_car_that_starts_in_the_pits_counts_from_its_first_crossing_after_leaving()
    {
        var laps = new CleanLaps();
        laps.Observe("7", 0, true, true); // out-lap from the pit stall: not followed yet
        laps.Observe("7", 0, false, true);
        laps.Observe("7", 1, false, true); // the out-lap ends: not clean
        laps.Observe("7", 2, false, true);

        Assert.Equal([1], laps.Done["7"]);
    }

    [Fact]
    public void A_skipped_lap_count_or_a_car_leaving_the_world_ends_the_count()
    {
        var laps = new CleanLaps();
        laps.Observe("9", 0, false, true);
        laps.Observe("9", 1, false, true);
        laps.Observe("9", 3, false, true); // a jump (tow, reset): that lap is not counted
        laps.Observe("9", 4, false, true);
        laps.Observe("9", 4, false, false);

        Assert.Equal([3], laps.Done["9"]);
        Assert.False(laps.Finished(1), "nobody is left on track");
    }
}
