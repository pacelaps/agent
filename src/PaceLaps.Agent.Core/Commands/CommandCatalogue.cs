namespace PaceLaps.Agent.Core.Commands;

/// <summary>
/// The command catalogue's idempotent flag (spec: Command catalogue; the web service's CommandCatalogue): sending one
/// of these twice does no harm. The server says so on every command it sends; the local failsafe, which has no server,
/// reads it here to decide whether a failed command may be tried again.
/// </summary>
public static class CommandCatalogue
{
    private static readonly HashSet<string> IdempotentTypes = new(StringComparer.Ordinal)
    {
        "yellow", "pit_close", "pit_open", "restart_single", "restart_double", "clear_all", "clear", "pace_laps_set", "replay_jump", "replay_live",
    };

    public static bool Idempotent(string type) => IdempotentTypes.Contains(type);
}
