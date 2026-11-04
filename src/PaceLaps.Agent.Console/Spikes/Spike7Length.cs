using System.Globalization;
using System.Text;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Spike 7: what is the maximum chat message length? Sends ruler texts of increasing length through the typed
/// channel, once with /rc and once as plain chat (the path announcements take). In a ruler every number ends at
/// the character position it names, so the last number the operator can read is the length that arrived.
/// </summary>
internal static class Spike7Length
{
    public static readonly int[] DefaultLengths = [60, 100, 128, 160, 200, 255, 256, 300, 400];

    public static async Task<int> RunAsync(SpikeEnvironment env)
    {
        var lengths = ParseLengths(env.Args.Get("lengths")) ?? DefaultLengths;
        var prefixes = new[] { "/rc", "" };
        var options = env.TypedChatOptionsFromArgs();
        if (!await env.ConnectAsync([]) || !env.PreflightTyping())
        {
            return 1;
        }

        using var log = env.CreateLog(7);
        log.Write("header", env.Header(7, log.RunId, new { lengths, prefixes }));
        Console.WriteLine($"Log: {log.Path}");
        var sender = env.CreateSender(options);
        Prompt.Heading($"Spike 7: {lengths.Length * prefixes.Length} messages. After each, read the last number you can see in chat.");
        Console.WriteLine("Example: '......10......20...' ending at 'x200' means 200 characters arrived.");

        var answers = new List<(string Prefix, int Length, int Seen)>();
        foreach (var prefix in prefixes)
        {
            foreach (var length in lengths)
            {
                var text = Ruler(prefix, length);
                var label = prefix.Length == 0 ? "plain" : prefix;
                if (Prompt.Ask($"  {label} {length}: [Enter] send, [s] skip:").Equals("s", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = await sender.SendAsync(text, env.Cancellation);
                if (!result.Success)
                {
                    Prompt.Warn($"  send failed: {result.Failure} {result.Detail}");
                    log.Write("message", new { prefix = label, length, sent = false, failure = result.Failure, detail = result.Detail });
                    continue;
                }

                var seen = Prompt.Int("  Last number visible in chat (0 if the message did not appear)", 0, 9999);
                var lines = Prompt.Ask("  Shown on one line, wrapped, or split into several messages? [1/w/s]:");
                answers.Add((label, length, seen));
                log.Write("message", new { prefix = label, length, sent = true, seen, lines, typingMs = Stats.Ms(result.TypingTime), totalMs = Stats.Ms(result.TotalTime) });
            }
        }

        var limits = answers.GroupBy(a => a.Prefix).ToDictionary(g => g.Key, g => EstimateLimit(g.Select(a => (a.Length, a.Seen)).ToList()));
        var notes = Prompt.Ask("Notes (blank for none):");
        var result2 = limits.Count > 0 && limits.Values.All(l => l is not null) ? "pass" : "partial";
        log.Write("summary", new { result = result2, limits, notes });
        foreach (var (prefix, limit) in limits)
        {
            Console.WriteLine($"  {prefix}: {(limit is { } l ? $"limit about {l} characters" : "no limit seen up to the longest message")}");
        }

        Console.WriteLine($"Result: {result2}. Log: {log.Path}");
        return 0;
    }

    /// <summary>
    /// A text of exactly <paramref name="length"/> characters starting with the prefix and a space (if any). Each
    /// number ends at the 1-based position it names: "/rc ....10.......20".
    /// </summary>
    public static string Ruler(string prefix, int length)
    {
        var sb = new StringBuilder(prefix.Length == 0 ? "" : prefix + " ");
        while (sb.Length < length)
        {
            var mark = (sb.Length / 10 + 1) * 10;
            var label = mark.ToString(CultureInfo.InvariantCulture);
            if (mark > length)
            {
                sb.Append('.', length - sb.Length);
                break;
            }

            var dots = mark - sb.Length - label.Length;
            if (dots < 0)
            {
                // Not enough room before this mark (right after the prefix): fill up to the next mark instead.
                sb.Append('.', mark - sb.Length);
                continue;
            }

            sb.Append('.', dots).Append(label);
        }

        return sb.ToString();
    }

    /// <summary>
    /// The largest number seen when a longer message was cut, or null if nothing was cut. A message counts as cut
    /// when the last visible mark is more than 10 below the sent length.
    /// </summary>
    public static int? EstimateLimit(IReadOnlyList<(int Sent, int Seen)> answers)
    {
        var cut = answers.Where(a => a.Seen > 0 && a.Seen < a.Sent - 10).ToList();
        return cut.Count == 0 ? null : cut.Max(a => a.Seen);
    }

    private static int[]? ParseLengths(string? s) =>
        s is null ? null : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 10 and <= 2000
                ? n : throw new Cli.CliException("--lengths must be numbers from 10 to 2000, comma separated"))
            .ToArray();
}
