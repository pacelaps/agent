using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaceLaps.Agent.Core.Commands;

/// <summary>Which channels may carry a command (spec: Interface contracts &gt; Command catalogue).</summary>
public enum ChannelOptions
{
    /// <summary>A fixed chat line: a verified macro slot with exactly this text, else the typed channel.</summary>
    MacroOrTyped,
    TypedOnly,
    Sdk,
}

/// <summary>A replay jump target for the SDK channel; <paramref name="Car"/>: the camera follows that car.</summary>
public sealed record ReplayTarget(int SessionNum, double SessionTimeSec, string? Car = null);

/// <param name="ChatText">The exact chat line, or null for SDK commands.</param>
/// <param name="IdempotentByCatalogue">The catalogue's idempotent flag for this type.</param>
/// <param name="SyntaxConfirmed">True when the chat syntax was confirmed in the sim (spike 3).</param>
/// <param name="Car">The target car number, for commands that name one.</param>
public sealed record RenderedCommand(string Type, string? ChatText, ChannelOptions Channels, bool IdempotentByCatalogue,
    bool SyntaxConfirmed, ReplayTarget? Replay = null, string? Car = null);

public sealed record RenderResult(RenderedCommand? Command, string? Error)
{
    public bool Ok => Command is not null;

    public static RenderResult Fail(string error) => new(null, error);
}

/// <summary>
/// Limits the spikes decide. The defaults are provisional until then and come from the agent's configuration.
/// </summary>
public sealed record CommandLimits
{
    /// <summary>Longest chat line the agent sends. Spike 7: the sim cuts at about 254 characters; 250 leaves a margin.</summary>
    public int MaxChatLength { get; init; } = 250;

    /// <summary>Highest pace-lap count. Spike 9: !pacelaps 99 gave 20 laps, 20 was accepted.</summary>
    public int MaxPaceLaps { get; init; } = 20;

    /// <summary>Longest optional message (command.schema.json): the longest command prefix plus this stays under 250.</summary>
    public int MaxMessageLength { get; init; } = 230;

    public int MaxStopAndGoSeconds { get; init; } = 300;
}

/// <summary>
/// Renders the chat text of a structured command from a fixed allow-list and validates every parameter.
/// Anything not in the allow-list, any unknown parameter and any invalid value is rejected with a reason.
/// The agent never sends text the server wrote, except the free text of announce and the optional message.
/// </summary>
public sealed partial class CommandRenderer
{
    private readonly CommandLimits _limits;

    public CommandRenderer(CommandLimits? limits = null)
    {
        _limits = limits ?? new CommandLimits();
    }

    public CommandLimits Limits => _limits;

    /// <summary>The command types the agent knows, as in command.schema.json.</summary>
    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "yellow", "pit_close", "pit_open", "restart_single", "restart_double", "clear_all", "clear",
        "pace_laps_set", "pace_laps_add", "black_flag", "wave_by", "eol", "dq", "remove", "announce", "replay_jump",
        "replay_live",
    };

    /// <summary>
    /// View commands (race control's incident review, spec-feedback 41): they change only this PC's view, never the race,
    /// so an active agent runs them whether it is the executor or not, without the epoch check.
    /// </summary>
    public static readonly IReadOnlySet<string> ViewTypes = new HashSet<string>(StringComparer.Ordinal) { "replay_jump", "replay_live" };

    [GeneratedRegex(@"^[0-9]{1,3}$")]
    private static partial Regex CarNumber();

    /// <summary>Letters (any language), digits, space and common punctuation. No control characters.</summary>
    [GeneratedRegex(@"^[\p{L}\p{N} .,:;!?\-'""()/+#%&*=_@]+$")]
    private static partial Regex FreeText();

    public RenderResult Render(string type, JsonElement parameters)
    {
        if (!KnownTypes.Contains(type))
        {
            return RenderResult.Fail($"unknown command type '{type}'");
        }

        var p = parameters.ValueKind == JsonValueKind.Undefined || parameters.ValueKind == JsonValueKind.Null
            ? new Params(default)
            : parameters.ValueKind == JsonValueKind.Object ? new Params(parameters) : null;
        if (p is null)
        {
            return RenderResult.Fail("params must be an object");
        }

        try
        {
            return type switch
            {
                "yellow" => WithOptionalMessage(p, "yellow", "!yellow", ChannelOptions.MacroOrTyped, idempotent: true, confirmed: true),
                "pit_close" => Fixed(p, type, "!pitclose"),
                "pit_open" => Fixed(p, type, "!pitopen"),
                "restart_single" => Fixed(p, type, "!restart single"),
                "restart_double" => Fixed(p, type, "!restart double"),
                "clear_all" => Fixed(p, type, "!clearall"),
                "clear" => Car(p, type, "!clear", idempotent: true, confirmed: true, message: false),
                "pace_laps_set" => PaceLaps(p, type, add: false),
                "pace_laps_add" => PaceLaps(p, type, add: true),
                "black_flag" => BlackFlag(p),
                "wave_by" => Car(p, type, "!waveby", idempotent: false, confirmed: true, message: true),
                "eol" => Car(p, type, "!eol", idempotent: false, confirmed: true, message: true),
                "dq" => Car(p, type, "!dq", idempotent: false, confirmed: false, message: true),
                "remove" => Car(p, type, "!remove", idempotent: false, confirmed: false, message: true),
                "announce" => Announce(p),
                "replay_jump" => Replay(p),
                "replay_live" => ReplayLive(p),
                _ => RenderResult.Fail($"unknown command type '{type}'"),
            };
        }
        catch (InvalidParameterException e)
        {
            return RenderResult.Fail(e.Message);
        }
    }

    private RenderResult Fixed(Params p, string type, string text)
    {
        p.AllowOnly();
        return Ok(new RenderedCommand(type, text, ChannelOptions.MacroOrTyped, IdempotentByCatalogue: true, SyntaxConfirmed: true));
    }

    private RenderResult WithOptionalMessage(Params p, string type, string command, ChannelOptions channels, bool idempotent, bool confirmed)
    {
        p.AllowOnly("message");
        var message = p.OptionalString("message");
        if (message is null)
        {
            return Ok(new RenderedCommand(type, command, channels, idempotent, confirmed));
        }

        // A message makes the line variable, so it can only be typed.
        return Ok(new RenderedCommand(type, command + " " + CheckFreeText(message, "message", command.Length + 1), ChannelOptions.TypedOnly, idempotent, confirmed));
    }

    private RenderResult Car(Params p, string type, string command, bool idempotent, bool confirmed, bool message)
    {
        if (message)
        {
            p.AllowOnly("car", "message");
        }
        else
        {
            p.AllowOnly("car");
        }

        var car = CheckCar(p.RequiredString("car"));
        var line = $"{command} #{car}";
        var text = message ? p.OptionalString("message") : null;
        if (text is not null)
        {
            line += " " + CheckFreeText(text, "message", line.Length + 1);
        }

        return Ok(new RenderedCommand(type, line, ChannelOptions.TypedOnly, idempotent, confirmed, Car: car));
    }

    private RenderResult PaceLaps(Params p, string type, bool add)
    {
        p.AllowOnly("n");
        var n = p.RequiredInt("n");
        if (n < 1 || n > _limits.MaxPaceLaps)
        {
            throw new InvalidParameterException($"n must be 1 to {_limits.MaxPaceLaps} (spike 9: the sim's maximum is 20)");
        }

        var text = add ? $"!pacelaps +{n}" : $"!pacelaps {n}";

        // The relative form is not idempotent and must never come from a fixed macro.
        return Ok(new RenderedCommand(type, text, add ? ChannelOptions.TypedOnly : ChannelOptions.MacroOrTyped, IdempotentByCatalogue: !add, SyntaxConfirmed: true));
    }

    private RenderResult BlackFlag(Params p)
    {
        p.AllowOnly("car", "mode", "seconds", "laps");
        var car = CheckCar(p.RequiredString("car"));
        var mode = p.RequiredString("mode");
        if (p.Has("laps"))
        {
            throw new InvalidParameterException("black flag with laps is not supported yet: its chat syntax is not confirmed (spike 3 tested seconds and drive-through)");
        }

        switch (mode)
        {
            case "drive_through":
                if (p.Has("seconds"))
                {
                    throw new InvalidParameterException("seconds is not allowed for a drive-through");
                }

                return Ok(new RenderedCommand("black_flag", $"!black #{car} D", ChannelOptions.TypedOnly, false, true, Car: car));
            case "stop_and_go":
                var seconds = p.Has("seconds") ? p.RequiredInt("seconds") : throw new InvalidParameterException("stop_and_go needs seconds");
                if (seconds < 1 || seconds > _limits.MaxStopAndGoSeconds)
                {
                    throw new InvalidParameterException($"seconds must be 1 to {_limits.MaxStopAndGoSeconds}");
                }

                return Ok(new RenderedCommand("black_flag", $"!black #{car} {seconds.ToString(CultureInfo.InvariantCulture)}", ChannelOptions.TypedOnly, false, true, Car: car));
            default:
                throw new InvalidParameterException($"mode must be stop_and_go or drive_through, not '{mode}'");
        }
    }

    private RenderResult Announce(Params p)
    {
        p.AllowOnly("text");
        var text = CheckFreeText(p.RequiredString("text"), "text", 0);

        // Plain chat: a leading ! or / would make the announcement a command.
        if (text[0] is '!' or '/')
        {
            throw new InvalidParameterException("text must not start with ! or /, which would make it a chat command");
        }

        return Ok(new RenderedCommand("announce", text, ChannelOptions.TypedOnly, IdempotentByCatalogue: false, SyntaxConfirmed: true));
    }

    private static RenderResult Replay(Params p)
    {
        p.AllowOnly("sessionNum", "sessionTime", "car");
        var num = p.RequiredInt("sessionNum");
        var time = p.RequiredNumber("sessionTime");
        if (num < 0 || time < 0)
        {
            throw new InvalidParameterException("sessionNum and sessionTime must not be negative");
        }

        var car = p.OptionalString("car") is { } c ? CheckCar(c) : null;
        return Ok(new RenderedCommand("replay_jump", null, ChannelOptions.Sdk, IdempotentByCatalogue: true, SyntaxConfirmed: true,
            new ReplayTarget(num, time, car), car));
    }

    private static RenderResult ReplayLive(Params p)
    {
        p.AllowOnly();
        return Ok(new RenderedCommand("replay_live", null, ChannelOptions.Sdk, IdempotentByCatalogue: true, SyntaxConfirmed: true));
    }

    private static string CheckCar(string car) =>
        CarNumber().IsMatch(car) ? car : throw new InvalidParameterException($"car must be one to three digits, not '{car}'");

    /// <param name="prefixLength">Characters before the free text on the chat line, for the length limit.</param>
    private string CheckFreeText(string text, string name, int prefixLength)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            throw new InvalidParameterException($"{name} must not be empty");
        }

        if (!FreeText().IsMatch(t))
        {
            throw new InvalidParameterException($"{name} contains characters that are not allowed (letters, digits, space and . , : ; ! ? - ' \" ( ) / + # % & * = _ @)");
        }

        if (name == "message" && t.Length > _limits.MaxMessageLength)
        {
            throw new InvalidParameterException($"message is {t.Length} characters, the limit is {_limits.MaxMessageLength}");
        }

        if (prefixLength + t.Length > _limits.MaxChatLength)
        {
            throw new InvalidParameterException($"{name} is too long: the chat line would be {prefixLength + t.Length} characters, the limit is {_limits.MaxChatLength}");
        }

        return t;
    }

    private static RenderResult Ok(RenderedCommand c) => new(c, null);

    private sealed class InvalidParameterException(string message) : Exception(message);

    /// <summary>Typed access to the params object; unknown fields and wrong types are errors.</summary>
    private sealed class Params(JsonElement element)
    {
        private bool IsEmpty => element.ValueKind != JsonValueKind.Object;

        public bool Has(string name) => !IsEmpty && element.TryGetProperty(name, out _);

        public void AllowOnly(params string[] names)
        {
            if (IsEmpty)
            {
                return;
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (!names.Contains(prop.Name, StringComparer.Ordinal))
                {
                    throw new InvalidParameterException($"parameter '{prop.Name}' is not allowed");
                }
            }
        }

        public string RequiredString(string name) =>
            OptionalString(name) ?? throw new InvalidParameterException($"parameter '{name}' is required");

        public string? OptionalString(string name)
        {
            if (IsEmpty || !element.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return v.ValueKind == JsonValueKind.String ? v.GetString() : throw new InvalidParameterException($"parameter '{name}' must be a string");
        }

        public int RequiredInt(string name)
        {
            if (IsEmpty || !element.TryGetProperty(name, out var v))
            {
                throw new InvalidParameterException($"parameter '{name}' is required");
            }

            return v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
                ? n
                : throw new InvalidParameterException($"parameter '{name}' must be a whole number");
        }

        public double RequiredNumber(string name)
        {
            if (IsEmpty || !element.TryGetProperty(name, out var v))
            {
                throw new InvalidParameterException($"parameter '{name}' is required");
            }

            return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new InvalidParameterException($"parameter '{name}' must be a number");
        }
    }
}
