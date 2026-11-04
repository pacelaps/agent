using PaceLaps.Agent.Core.Config;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// config.json next to the credentials: this PC's settings (macro slots and the sim's paths belong to the PC). A missing
/// file means the defaults; a file with errors stops the agent with every error named.
/// </summary>
public sealed class ConfigStore(string path)
{
    public static string DefaultPath { get; } = AgentPaths.File("config.json");

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    /// <exception cref="SettingsFileException">The file has errors.</exception>
    public AgentConfig Load()
    {
        var (config, errors) = TryLoad();
        return errors.Count == 0 ? config : throw new SettingsFileException(Path, errors);
    }

    /// <summary>The settings, or the errors in the file (the tray app shows them instead of stopping).</summary>
    public (AgentConfig Config, IReadOnlyList<string> Errors) TryLoad() => Exists ? AgentConfig.Parse(File.ReadAllText(Path)) : (new AgentConfig(), []);

    public void Save(AgentConfig config)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, config.ToJson());
    }
}

/// <summary>config.json has errors; the message names every one.</summary>
public sealed class SettingsFileException(string path, IReadOnlyList<string> errors)
    : Exception($"{path} has errors:\n  " + string.Join("\n  ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
