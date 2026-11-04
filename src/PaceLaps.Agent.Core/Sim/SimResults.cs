namespace PaceLaps.Agent.Core.Sim;

/// <summary>
/// One session's results as iRacing's session info has them (SessionInfo.Sessions[]: ResultsPositions and the results
/// fields), with iRacing's own meanings (agent spike-results.md, "Results check"): <see cref="SimResultRow.Time"/> is
/// the gap to the leader, <see cref="SimResultRow.Lap"/> the laps behind, the class position counts from 0, times are
/// -1 while there are none.
/// </summary>
public sealed record SimSessionResults(int SessionNum, bool Official, int LapsComplete, SimFastestLap? FastestLap, IReadOnlyList<SimResultRow> Rows);

/// <summary>ResultsFastestLap: car index 255 or a time of -1 while there is none.</summary>
public sealed record SimFastestLap(int CarIdx, int Lap, double Time);

/// <summary>A row of ResultsPositions.</summary>
public sealed record SimResultRow(int CarIdx, int Position, int ClassPosition, int LapsComplete, int Lap, double Time,
    double LastTime, double FastestTime, int FastestLap, int LapsLed, int Incidents, int ReasonOutId, string? ReasonOutStr);
