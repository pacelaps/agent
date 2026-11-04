namespace PaceLaps.Agent.Hosting;

/// <summary>The web service's address as the operator types it: HTTPS, or plain HTTP for this PC only (development).</summary>
public static class ServerAddress
{
    public static bool TryParse(string? raw, out Uri url, out string error)
    {
        url = null!;
        error = "";
        var text = raw?.Trim() ?? "";
        if (!Uri.TryCreate(text.TrimEnd('/') + "/", UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && !(parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            error = $"The server must be https:// (http only for localhost), not '{text}'.";
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>Same scheme, host and port: credentials paired with one are good for the other.</summary>
    public static bool Same(Uri a, Uri b) =>
        string.Equals(a.GetLeftPart(UriPartial.Authority), b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
