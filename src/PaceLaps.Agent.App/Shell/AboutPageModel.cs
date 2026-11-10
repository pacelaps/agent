using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;

namespace PaceLaps.Agent.App.Shell;

/// <summary>About and help: the version, the Race Control it belongs to, where to read more or ask, and the licences.</summary>
public sealed partial class AboutPageModel(string version, Uri? server)
{
    public const string Guide = "https://pacelaps.com/docs/race-control/agent";
    public const string Troubleshooting = "https://pacelaps.com/docs/race-control/troubleshooting";
    public const string Download = "https://pacelaps.com/download";
    public const string Source = "https://github.com/pacelaps/agent";
    public const string Support = "support@pacelaps.com";

    public string Version { get; } = $"Version {version}";

    public string Server { get; } = server is null ? "Development build: pairs with any Race Control" : $"For {server.Host}";

    [RelayCommand]
    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();

    /// <summary>The licence and notices that come with the program (next to the exe), else the source's.</summary>
    [RelayCommand]
    private static void OpenLicences()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
        Open(File.Exists(local) ? local : Source + "/blob/main/THIRD-PARTY-NOTICES.md");
    }
}
