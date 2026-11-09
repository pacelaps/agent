using System.Text.Json;

namespace PaceLaps.Agent.Core.Updates;

/// <summary>A newer release than the running one, with its changelog lines (empty when the changelog has none).</summary>
public sealed record UpdateOffer(string Version, IReadOnlyList<string> Lines);

/// <summary>
/// Decides from the download server's two public files whether to offer an update (the agent's update notice):
/// <c>agent/latest.json</c> (the newest release, written by
/// scripts/upload-downloads.ps1) and <c>agent/changelog.json</c> (CHANGELOG.md's releases). A version that cannot
/// be read, or a document of another kind, offers nothing. Early releases (0.x or a prerelease) are offered only
/// to agents that run an early release themselves.
/// </summary>
public static class UpdateCheck
{
    public static UpdateOffer? Offer(string currentVersion, string latestJson, string? changelogJson)
    {
        var current = ReleaseVersion.TryParse(currentVersion);
        if (current is null)
        {
            return null;
        }

        string? latestText;
        try
        {
            using var doc = JsonDocument.Parse(latestJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Is(root, "v", 1) || !IsText(root, "product", "agent"))
            {
                return null;
            }

            latestText = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }

        var latest = ReleaseVersion.TryParse(latestText);
        if (latest is null || latest <= current || (latest.IsEarly && !current.IsEarly))
        {
            return null;
        }

        return new UpdateOffer(latest.ToString(), Lines(changelogJson, latest));
    }

    private static IReadOnlyList<string> Lines(string? changelogJson, ReleaseVersion version)
    {
        if (changelogJson is null)
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(changelogJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !Is(root, "v", 1)
                || !root.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var release in releases.EnumerateArray())
            {
                if (release.ValueKind == JsonValueKind.Object
                    && release.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                    && ReleaseVersion.TryParse(v.GetString()) is { } rv && rv.CompareTo(version) == 0
                    && release.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                {
                    return changes.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                        .Select(c => c.GetString()!.Trim()).Where(c => c.Length > 0).Take(20).ToList();
                }
            }
        }
        catch (JsonException)
        {
        }

        return [];
    }

    private static bool Is(JsonElement root, string name, int value) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n) && n == value;

    private static bool IsText(JsonElement root, string name, string value) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() == value;
}
