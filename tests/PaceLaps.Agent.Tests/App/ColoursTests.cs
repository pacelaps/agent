using System.Text.RegularExpressions;

namespace PaceLaps.Agent.Tests.App;

/// <summary>
/// The app's colours (src/PaceLaps.Agent.App/Themes/Colors.xaml) are Race Control's dark theme: each token there must
/// have the value of the web service's tokens file when that repository sits next to this one (the developer machine;
/// CI has only this repository and skips the comparison).
/// </summary>
public sealed partial class ColoursTests
{
    [Fact]
    public void The_app_colours_are_race_controls_dark_tokens()
    {
        var repo = FindRepoRoot();
        var tokensFile = Path.Combine(Path.GetDirectoryName(repo)!, "webservice", "resources", "css", "tokens.css");
        if (!File.Exists(tokensFile))
        {
            Assert.Skip("webservice repository not next to this one");
        }

        var css = File.ReadAllText(tokensFile);
        var dark = css[css.IndexOf(".dark", StringComparison.Ordinal)..];
        var tokens = TokenPattern().Matches(dark).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant(), StringComparer.Ordinal);
        var app = AppColourPattern().Matches(File.ReadAllText(Path.Combine(repo, "src", "PaceLaps.Agent.App", "Themes", "Colors.xaml")))
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant(), StringComparer.Ordinal);

        Assert.NotEmpty(app);
        var differences = app.Where(c => !tokens.TryGetValue(c.Key, out var value) || value != c.Value)
            .Select(c => $"{c.Key}: app {c.Value}, tokens {tokens.GetValueOrDefault(c.Key, "missing")}").ToList();
        Assert.Empty(differences);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PaceLaps.Agent.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("repository root not found");
    }

    [GeneratedRegex(@"--pl-([a-z-]+):\s*(#[0-9a-fA-F]{6});")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"<Color x:Key=""([a-z-]+)"">(#[0-9A-Fa-f]{6})</Color>")]
    private static partial Regex AppColourPattern();
}
