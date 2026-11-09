using System.Globalization;

namespace PaceLaps.Agent.Core.Updates;

/// <summary>
/// A release version as the agent's releases name them: <c>0.12.0</c>, <c>1.0.0-rc.1</c>, and the build's own
/// <c>0.12.0+1a2b3c4</c>. Ordered as SemVer orders them: the build part after <c>+</c> is ignored, a prerelease sorts
/// before its release, and its parts compare numerically when both are numbers.
/// </summary>
public sealed record ReleaseVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<ReleaseVersion>
{
    /// <summary>A 0.x version or one with a prerelease part: early releases, offered to agents of their kind.</summary>
    public bool IsEarly => Major == 0 || Prerelease is not null;

    public static ReleaseVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var s = text.Trim();
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            s = s[..plus];
        }

        string? pre = null;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return null;
            }
        }

        var parts = s.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return new ReleaseVersion(numbers[0], numbers[1], numbers[2], pre);
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var c = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (c != 0)
        {
            return c;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => ComparePrerelease(Prerelease!, other.Prerelease!),
        };
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Prerelease is null ? "" : $"-{Prerelease}");

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    private static int ComparePrerelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xNumber = int.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNumber = int.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            var c = (xNumber, yNumber) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (c != 0)
            {
                return c;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
