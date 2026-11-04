namespace PaceLaps.Agent.Hosting;

/// <summary>
/// Where the agent keeps its files on this PC: %LOCALAPPDATA%\PaceLaps\Agent. For development the environment
/// variable PACELAPS_AGENT_DATA names another folder, so a test agent paired with the development server keeps its
/// own pairing, settings and logs and leaves the operator's (one server per credentials file) alone.
/// </summary>
public static class AgentPaths
{
    public const string OverrideVariable = "PACELAPS_AGENT_DATA";

    public static string Directory { get; } = Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } other
        ? Path.GetFullPath(other)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaceLaps", "Agent");

    public static string File(string name) => Path.Combine(Directory, name);
}
