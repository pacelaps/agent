namespace PaceLaps.Agent.Core.Agent;

/// <summary>
/// The version the agent reports (agentVersion in hello and pairing_request; the contract allows 40 characters).
/// The .NET SDK appends the full commit hash to the informational version ("0.1.0+" and 40 hex digits); the agent
/// keeps the short form git shows ("0.1.0+1a2b3c4").
/// </summary>
public static class AgentVersion
{
    public const int MaxLength = 40;

    public static string Shorten(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
        {
            return "unknown";
        }

        var version = informational.Trim();
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0 && version.Length - plus - 1 > 7)
        {
            version = version[..(plus + 8)];
        }

        return version.Length <= MaxLength ? version : version[..MaxLength];
    }
}
