using System.Reflection;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// What a release build carries: the server it belongs to (the release workflow builds it in with
/// <c>-p:ReleaseServer=...</c>). Downloaded agents always pair with that server; local builds have none and let the
/// operator type any address.
/// </summary>
public static class ReleaseBuild
{
    public const string ServerKey = "ReleaseServer";

    /// <summary>The server built into this program, or null (a local build).</summary>
    public static Uri? Server(Assembly program)
    {
        var value = program.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == ServerKey)?.Value;
        return ServerAddress.TryParse(value, out var url, out _) ? url : null;
    }
}
