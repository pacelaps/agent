using System.Globalization;
using System.Text.Json;
using PaceLaps.Agent.Core.Commands;

namespace PaceLaps.Agent.Core.Config;

/// <summary>
/// The operator's settings, kept in <c>config.json</c> next to the credentials (spec: Packaging, "a config file"): the
/// server, the league to report in a hosted session, the macro channel with its slots, the local failsafe and its
/// delay, where the sim's app.ini is, whether typed commands go without the focus switch, and whether to look for updates. The tray app's settings screen edits the same file; the console's options
/// override it. Parsing reports every bad setting by name instead of failing on the first.
/// </summary>
public sealed record AgentConfig
{
    public Uri? Server { get; init; }

    /// <summary>The iRacing league id to report in a hosted session, which has none (spike 11 fallback).</summary>
    public int? League { get; init; }

    public MacroSettings Macros { get; init; } = new();

    public LocalFailsafeSettings LocalFailsafe { get; init; } = new();

    /// <summary>The sim's app.ini (macro texts, self-test step 6); null for the default location.</summary>
    public string? AppIni { get; init; }

    /// <summary>
    /// Typed commands without bringing iRacing to the front (spec-feedback 37). On by default; off types every line
    /// the focus way, as before, should an iRacing build stop accepting the posted way.
    /// </summary>
    public bool TypedWithoutFocus { get; init; } = true;

    /// <summary>
    /// Look for a newer release once a day (the update notice; downloaded builds only). On by default; it reads two
    /// public files from the download server and sends nothing about the agent.
    /// </summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Reads config.json. Unknown or invalid settings are errors; missing ones take their defaults.</summary>
    public static (AgentConfig Config, IReadOnlyList<string> Errors) Parse(string json)
    {
        var errors = new List<string>();
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }).RootElement;
        }
        catch (JsonException e)
        {
            return (new AgentConfig(), [$"not valid JSON: {e.Message}"]);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return (new AgentConfig(), ["the file must hold one JSON object"]);
        }

        var config = new AgentConfig();
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "server":
                    config = config with { Server = ReadServer(p.Value, errors) };
                    break;
                case "league":
                    config = config with { League = ReadInt(p.Value, "league", 1, int.MaxValue, errors) };
                    break;
                case "macros":
                    config = config with { Macros = MacroSettings.Read(p.Value, errors) };
                    break;
                case "localFailsafe":
                    config = config with { LocalFailsafe = LocalFailsafeSettings.Read(p.Value, errors) };
                    break;
                case "appIni":
                    config = config with { AppIni = p.Value.ValueKind == JsonValueKind.Null ? null : ReadString(p.Value, "appIni", errors) };
                    break;
                case "typedWithoutFocus":
                    config = config with { TypedWithoutFocus = ReadBool(p.Value, "typedWithoutFocus", errors) };
                    break;
                case "checkForUpdates":
                    config = config with { CheckForUpdates = ReadBool(p.Value, "checkForUpdates", errors) };
                    break;
                default:
                    errors.Add($"unknown setting '{p.Name}'");
                    break;
            }
        }

        return (config, errors);
    }

    /// <summary>The file as written by the agent: every setting, so the operator sees what can be set.</summary>
    public string ToJson()
    {
        var doc = new Dictionary<string, object?>
        {
            ["server"] = Server?.ToString(),
            ["league"] = League,
            ["macros"] = new Dictionary<string, object?>
            {
                ["enabled"] = Macros.Enabled,
                ["slots"] = Macros.Slots.OrderBy(s => s.Key).ToDictionary(s => s.Key.ToString(CultureInfo.InvariantCulture), s => s.Value),
            },
            ["localFailsafe"] = new Dictionary<string, object?>
            {
                ["enabled"] = LocalFailsafe.Enabled,
                ["delaySeconds"] = LocalFailsafe.DelaySeconds,
            },
            ["appIni"] = AppIni,
            ["typedWithoutFocus"] = TypedWithoutFocus,
            ["checkForUpdates"] = CheckForUpdates,
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Plain HTTP only for this PC (development); everything else over HTTPS. The same rule as pairing.
    /// </summary>
    public static bool AcceptableServer(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    private static Uri? ReadServer(JsonElement v, List<string> errors)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (v.ValueKind != JsonValueKind.String || !Uri.TryCreate(v.GetString(), UriKind.Absolute, out var uri) || !AcceptableServer(uri))
        {
            errors.Add("server must be an https:// address (http:// only for localhost)");
            return null;
        }

        return uri;
    }

    internal static int? ReadInt(JsonElement v, string name, int min, int max, List<string> errors)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var n) || n < min || n > max)
        {
            errors.Add($"{name} must be a whole number from {min} to {max}");
            return null;
        }

        return n;
    }

    internal static bool ReadBool(JsonElement v, string name, List<string> errors)
    {
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return v.GetBoolean();
        }

        errors.Add($"{name} must be true or false");
        return false;
    }

    internal static string? ReadString(JsonElement v, string name, List<string> errors)
    {
        if (v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        errors.Add($"{name} must be text");
        return null;
    }
}

/// <summary>
/// The macro channel (spec: Command channels): off by default; the slots the agent may trigger, each with the exact
/// chat text the sim slot holds (ending in $ there). The operator's other slots stay theirs.
/// </summary>
public sealed record MacroSettings
{
    public const int MaxTextLength = 250;

    public bool Enabled { get; init; }

    public IReadOnlyDictionary<int, string> Slots { get; init; } = new Dictionary<int, string>();

    /// <summary>The slots as the command executor uses them; empty when the channel is off.</summary>
    public MacroTable ToTable() => Enabled ? new MacroTable(Slots.Select(s => (s.Key, s.Value))) : MacroTable.Empty;

    internal static MacroSettings Read(JsonElement v, List<string> errors)
    {
        var settings = new MacroSettings();
        if (v.ValueKind != JsonValueKind.Object)
        {
            errors.Add("macros must be an object with enabled and slots");
            return settings;
        }

        foreach (var p in v.EnumerateObject())
        {
            switch (p.Name)
            {
                case "enabled":
                    settings = settings with { Enabled = AgentConfig.ReadBool(p.Value, "macros.enabled", errors) };
                    break;
                case "slots":
                    settings = settings with { Slots = ReadSlots(p.Value, errors) };
                    break;
                default:
                    errors.Add($"unknown setting 'macros.{p.Name}'");
                    break;
            }
        }

        return settings;
    }

    private static Dictionary<int, string> ReadSlots(JsonElement v, List<string> errors)
    {
        var slots = new Dictionary<int, string>();
        if (v.ValueKind != JsonValueKind.Object)
        {
            errors.Add("macros.slots must map slot numbers to chat texts, for example {\"14\": \"!pitclose\"}");
            return slots;
        }

        foreach (var p in v.EnumerateObject())
        {
            if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var slot) || slot is < 1 or > 15)
            {
                errors.Add($"macros.slots: '{p.Name}' is not a slot from 1 to 15");
                continue;
            }

            var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : "";
            if (text.Length is 0 or > MaxTextLength)
            {
                errors.Add($"macros.slots.{slot}: the chat text must be 1 to {MaxTextLength} characters");
                continue;
            }

            if (slots.Values.Any(t => t.TrimEnd('$') == text.TrimEnd('$')))
            {
                errors.Add($"macros.slots.{slot}: the same text is in another slot");
                continue;
            }

            slots[slot] = text;
        }

        return slots;
    }
}

/// <summary>
/// The local failsafe (spec: Local failsafe, opt-in): off by default. When on, the agent runs the league's copied
/// procedure after this long without the server while active in an armed session; agents further down the stagger wait
/// longer.
/// </summary>
public sealed record LocalFailsafeSettings
{
    public const int MinDelaySeconds = 10;
    public const int MaxDelaySeconds = 300;
    public const int DefaultDelaySeconds = 30;

    public bool Enabled { get; init; }

    public int DelaySeconds { get; init; } = DefaultDelaySeconds;

    public TimeSpan Delay => TimeSpan.FromSeconds(DelaySeconds);

    internal static LocalFailsafeSettings Read(JsonElement v, List<string> errors)
    {
        var settings = new LocalFailsafeSettings();
        if (v.ValueKind != JsonValueKind.Object)
        {
            errors.Add("localFailsafe must be an object with enabled and delaySeconds");
            return settings;
        }

        foreach (var p in v.EnumerateObject())
        {
            switch (p.Name)
            {
                case "enabled":
                    settings = settings with { Enabled = AgentConfig.ReadBool(p.Value, "localFailsafe.enabled", errors) };
                    break;
                case "delaySeconds":
                    settings = settings with
                    {
                        DelaySeconds = AgentConfig.ReadInt(p.Value, "localFailsafe.delaySeconds", MinDelaySeconds, MaxDelaySeconds, errors) ?? DefaultDelaySeconds,
                    };
                    break;
                default:
                    errors.Add($"unknown setting 'localFailsafe.{p.Name}'");
                    break;
            }
        }

        return settings;
    }
}
