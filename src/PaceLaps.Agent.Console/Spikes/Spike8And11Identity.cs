namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Spike 8 (operator name and admin detection) and spike 11 (session identity fields). Both read the session info
/// and ask the operator to confirm what the sim shows. The operator's name is never logged, only whether it is
/// readable and its length.
/// </summary>
internal static class Spike8And11Identity
{
    public static async Task<int> RunSpike8Async(SpikeEnvironment env)
    {
        if (!await env.ConnectAsync([]))
        {
            return 1;
        }

        var s = env.Sim.Session!;
        var me = s.CarByIdx(s.DriverCarIdx);
        using var log = env.CreateLog(8);
        log.Write("header", env.Header(8, log.RunId, new { }));

        Prompt.Heading("Spike 8: operator name and admin detection");
        Console.WriteLine($"  Session type         {s.CurrentSessionType} ({s.SimMode})");
        Console.WriteLine($"  Your car index       {s.DriverCarIdx} ({(me is null ? "not in the driver list" : me.IsSpectator ? "spectator" : "driver")})");
        Console.WriteLine($"  Name readable        {(s.OperatorNameLength > 0 ? $"yes ({s.OperatorNameLength} characters)" : "no")}");
        Console.WriteLine($"  DriverIsAdmin        {s.DriverIsAdmin}");

        var nameMatches = s.OperatorNameLength > 0 && Prompt.YesNo("Is the session one where your own name shows in the sim's driver list (so the agent can read it)?");
        string adminAnswer;
        while (true)
        {
            adminAnswer = Prompt.Ask("Are you an admin in this session according to iRacing? [y/n/u for unknown]").ToLowerInvariant();
            if (adminAnswer is "y" or "n" or "u")
            {
                break;
            }
        }

        var notes = Prompt.Ask("Notes (blank for none):");
        var expectedAdmin = adminAnswer switch { "y" => true, "n" => false, _ => (bool?)null };
        var result = Evaluate8(s.OperatorNameLength > 0, s.DriverIsAdmin, expectedAdmin);
        log.Write("summary", new
        {
            result,
            nameReadable = s.OperatorNameLength > 0,
            nameLength = s.OperatorNameLength,
            nameMatches,
            driverIsAdmin = s.DriverIsAdmin,
            operatorSaysAdmin = expectedAdmin,
            operatorIsSpectator = me?.IsSpectator,
            sessionType = s.CurrentSessionType,
            notes,
        });
        Console.WriteLine($"Result for this session: {result}. Log: {log.Path}");
        return 0;
    }

    /// <summary>
    /// This run's part of spike 8: the name must be readable, and DriverIsAdmin must agree with what the operator
    /// knows. Spike 8 as a whole needs one run as admin (spike 3 runs) and one without admin rights.
    /// </summary>
    public static string Evaluate8(bool nameReadable, bool driverIsAdmin, bool? operatorSaysAdmin) =>
        !nameReadable ? "fail (name not readable)"
        : operatorSaysAdmin is null ? "partial (admin state unknown)"
        : operatorSaysAdmin == driverIsAdmin ? "pass" : "fail (DriverIsAdmin disagrees)";

    public static async Task<int> RunSpike11Async(SpikeEnvironment env)
    {
        if (!await env.ConnectAsync([]))
        {
            return 1;
        }

        var s = env.Sim.Session!;
        using var log = env.CreateLog(11);
        log.Write("header", env.Header(11, log.RunId, new { }));
        Prompt.Heading("Spike 11: session identity");
        Console.WriteLine($"  League id        {s.LeagueId}");
        Console.WriteLine($"  Season id        {s.SeasonId}");
        Console.WriteLine($"  Session id       {s.SessionId}");
        Console.WriteLine($"  Sub-session id   {s.SubSessionId}");
        Console.WriteLine($"  Session          {s.CurrentSessionNum} {s.CurrentSessionType} ({s.SimMode})");

        var kind = Prompt.Ask("What kind of session is this? [l] league, [h] hosted, [o] official/test:").ToLowerInvariant();
        int? leaguePageId = null;
        if (kind == "l")
        {
            var raw = Prompt.Ask("League id as shown on the league's page on iracing.com (blank if unknown):");
            leaguePageId = int.TryParse(raw, out var id) ? id : null;
        }

        var notes = Prompt.Ask("Notes (blank for none):");
        var result = Evaluate11(kind, s.LeagueId, leaguePageId);
        log.Write("summary", new { result, kind, s.LeagueId, s.SeasonId, s.SessionId, s.SubSessionId, leaguePageId, notes });
        Console.WriteLine($"Result: {result}. Log: {log.Path}");
        return 0;
    }

    /// <summary>Pass only in a league session where the league id is not zero and matches the league's page.</summary>
    public static string Evaluate11(string kind, int leagueId, int? leaguePageId) =>
        kind != "l" ? "not a league session (ids recorded, spike still open)"
        : leagueId == 0 ? "fail (league id is 0 in a league session)"
        : leaguePageId is null ? "partial (league id not compared)"
        : leaguePageId == leagueId ? "pass" : "fail (league id differs from the league page)";
}
