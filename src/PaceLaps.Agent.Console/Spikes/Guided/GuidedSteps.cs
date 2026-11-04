using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaceLaps.Agent.ConsoleHost.Spikes.Guided;

internal sealed record GuidedStep
{
    public required string Id { get; init; }

    /// <summary>Chat text with <c>{car}</c> and <c>{car2}</c> placeholders. Empty for a watch-only step.</summary>
    public string Command { get; init; } = "";

    /// <summary>Sends nothing: the step only watches (for example the restart after a caution) and records a verdict.</summary>
    public bool WatchOnly { get; init; }

    public string When { get; init; } = "";
    public string Observe { get; init; } = "";
    public string[] Expect { get; init; } = [];

    /// <summary>Which car the car-specific expectations refer to: "car" or "car2".</summary>
    public string Target { get; init; } = "car";

    /// <summary>
    /// An expectation kind. When set, the runner arms the step and sends it as soon as telemetry shows this
    /// condition, so timing does not depend on the operator's reaction (for example oneLapToGreen).
    /// </summary>
    public string? SendWhen { get; init; }

    /// <summary>An expectation kind that ends the watch early (5 s after it is seen), for example green.</summary>
    public string? EndWhen { get; init; }

    public int WatchSeconds { get; init; } = 30;
    public bool Optional { get; init; }

    public bool UsesCar2 => Command.Contains("{car2}", StringComparison.Ordinal) || (Target == "car2" && UsesCarExpectations);

    public bool UsesCar => Command.Contains("{car}", StringComparison.Ordinal) || (Target == "car" && UsesCarExpectations);

    private bool UsesCarExpectations => Expect.Concat(SendWhen is null ? [] : [SendWhen]).Any(Expectations.IsPerCar);

    public string Render(string? car, string? car2) =>
        Command.Replace("{car2}", car2 ?? "{car2}", StringComparison.Ordinal).Replace("{car}", car ?? "{car}", StringComparison.Ordinal);
}

/// <param name="FinalQuestions">Yes/no questions asked at the end of the run; the answers are logged.</param>
internal sealed record GuidedPlan(string? About, IReadOnlyList<GuidedStep> Steps, IReadOnlyList<string> FinalQuestions);

internal static partial class GuidedSteps
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    [GeneratedRegex(@"^[0-9]{1,3}$")]
    private static partial Regex CarNumberPattern();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Placeholder();

    /// <summary>iRacing car numbers as shown in the sim: one to three digits (leading zeros are significant).</summary>
    public static bool IsValidCarNumber(string? s) => s is not null && CarNumberPattern().IsMatch(s);

    /// <summary>A path as given, or a bare file name looked up in the Spikes folder next to the program.</summary>
    public static string Resolve(string path)
    {
        if (File.Exists(path) || Path.IsPathRooted(path))
        {
            return path;
        }

        var shipped = Path.Combine(AppContext.BaseDirectory, "Spikes", path);
        return File.Exists(shipped) ? shipped : path;
    }

    public static GuidedPlan Load(string path) => Parse(File.ReadAllText(Resolve(path)));

    public static GuidedPlan Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<PlanDto>(json, Json) ?? throw new InvalidDataException("empty steps file");
        var steps = doc.Steps ?? throw new InvalidDataException("steps file has no \"steps\"");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (string.IsNullOrWhiteSpace(s.Id) || !ids.Add(s.Id))
            {
                throw new InvalidDataException($"step id '{s.Id}' is empty or used twice");
            }

            if (string.IsNullOrWhiteSpace(s.Command) != s.WatchOnly)
            {
                throw new InvalidDataException(s.WatchOnly
                    ? $"step '{s.Id}' is watch-only but has a command"
                    : $"step '{s.Id}' has no command");
            }

            foreach (Match m in Placeholder().Matches(s.Command))
            {
                if (m.Value is not ("{car}" or "{car2}"))
                {
                    throw new InvalidDataException($"step '{s.Id}': unknown placeholder {m.Value}");
                }
            }

            foreach (var e in s.Expect.Concat(new[] { s.SendWhen, s.EndWhen }.OfType<string>()))
            {
                if (!Expectations.Known.Contains(e))
                {
                    throw new InvalidDataException($"step '{s.Id}': unknown expectation '{e}' (known: {string.Join(", ", Expectations.Known)})");
                }
            }

            if (s.Target is not ("car" or "car2"))
            {
                throw new InvalidDataException($"step '{s.Id}': target must be car or car2");
            }

            if (s.WatchSeconds is < 1 or > 900)
            {
                throw new InvalidDataException($"step '{s.Id}': watchSeconds must be 1 to 900");
            }
        }

        return new GuidedPlan(doc.About, steps, doc.FinalQuestions ?? []);
    }

    private sealed record PlanDto(string? About, List<GuidedStep>? Steps, List<string>? FinalQuestions);
}
