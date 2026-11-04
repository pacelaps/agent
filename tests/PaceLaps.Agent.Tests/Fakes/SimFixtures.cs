using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Tests.Fakes;

/// <summary>Builds sessions and frames for tests: a pace car at index 0 and race cars after it.</summary>
internal static class SimFixtures
{
    public static SimSession Session(int updateCount = 1, int leagueId = 12345, int sessionNum = 2, string type = "Race",
        params (int Idx, string Num, int? Incidents)[] cars)
    {
        var list = new List<SimCar> { new(0, "0", 11, true, false, -1, null) };
        list.AddRange((cars.Length == 0 ? [(1, "12", 0), (2, "33", 0), (3, "4", 0)] : cars)
            .Select(c => new SimCar(c.Idx, c.Num, 4098, false, false, 100000 + c.Idx, c.Incidents)));
        return new SimSession
        {
            UpdateCount = updateCount,
            BuildVersion = "2026.10.01.01",
            LeagueId = leagueId,
            SeasonId = 6789,
            SessionId = 111,
            SubSessionId = 222,
            SessionNum = sessionNum,
            SessionType = type,
            DriverCarIdx = 1,
            DriverIsAdmin = true,
            OperatorCustId = 100001,
            OperatorName = "Example Operator",
            PaceCarIdx = 0,
            Cars = list,
        };
    }

    public static SimCarState Car(int lapCompleted = 5, float dist = 0.5f, int surface = SimValues.SurfaceOnTrack, int pos = 1,
        uint carFlags = 0, uint paceFlags = 0, bool pit = false) =>
        new(lapCompleted + 1, lapCompleted, dist, pit, surface, pos, pos, carFlags, paceFlags);

    public static SimFrame Frame(uint flags = 0x10000000, int state = SimValues.StateRacing, double time = 812.4, int sessionNum = 2,
        int paceMode = 4, int tick = 1, TimeSpan captured = default, params (int Idx, SimCarState State)[] cars)
    {
        var array = Enumerable.Repeat(new SimCarState(0, 0, -1, false, SimValues.SurfaceNotInWorld, 0, 0, 0, 0), 64).ToArray();
        foreach (var (idx, s) in cars)
        {
            array[idx] = s;
        }

        return new SimFrame
        {
            Captured = captured,
            Tick = tick,
            SessionTime = time,
            SessionNum = sessionNum,
            SessionState = state,
            SessionFlags = flags,
            PaceMode = paceMode,
            Cars = array,
        };
    }
}
