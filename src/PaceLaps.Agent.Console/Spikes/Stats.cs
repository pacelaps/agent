using System.Globalization;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

internal static class Stats
{
    /// <summary>Nearest-rank percentile (0 to 100). NaN for an empty list.</summary>
    public static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return double.NaN;
        }

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    public static double Ms(TimeSpan t) => Math.Round(t.TotalMilliseconds, 1);

    /// <summary>Parses "3, 7-9" into {3, 7, 8, 9}. Blank means none. Numbers must be within 1..max.</summary>
    public static bool TryParseNumberList(string? text, int max, out SortedSet<int> numbers, out string? error)
    {
        numbers = [];
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (var raw in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Split('-');
            if (parts.Length is < 1 or > 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                || from < 1 || to > max || from > to)
            {
                error = $"'{raw}' is not a number or range within 1 to {max}";
                numbers = [];
                return false;
            }

            for (var n = from; n <= to; n++)
            {
                numbers.Add(n);
            }
        }

        return true;
    }
}
