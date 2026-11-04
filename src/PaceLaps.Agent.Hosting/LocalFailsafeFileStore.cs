using System.Text.Json;
using PaceLaps.Agent.Core.LocalFailsafe;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// local-failsafe.json next to the settings: the copy of the league's local failsafe, so an agent restarted during an
/// outage still holds it. A file that cannot be read counts as no copy (the server sends it again when it is back).
/// </summary>
public sealed class LocalFailsafeFileStore(string path) : ILocalFailsafeStore
{
    public static string DefaultPath { get; } = AgentPaths.File("local-failsafe.json");

    public string Path { get; } = path;

    public LocalFailsafeCopy? Load()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<LocalFailsafeCopy>(File.ReadAllText(Path)) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(LocalFailsafeCopy copy)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy));
        File.Move(temp, Path, overwrite: true);
    }

    public void Delete() => File.Delete(Path);
}
