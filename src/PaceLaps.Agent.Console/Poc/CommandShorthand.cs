using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PaceLaps.Agent.ConsoleHost.Poc;

/// <summary>
/// Turns a typed line like <c>black_flag 12 stop_and_go 10</c> into a command type and params, the way the
/// server will send them. It does not validate: the agent's renderer does that, exactly as for server commands.
/// </summary>
internal static class CommandShorthand
{
    public const string Help = """
          yellow [message]                 pit_close | pit_open | restart_single | restart_double | clear_all
          clear CAR                        pace_laps_set N | pace_laps_add N
          black_flag CAR drive_through     black_flag CAR stop_and_go SECONDS
          wave_by | eol | dq | remove CAR [message]
          announce TEXT                    replay_jump SESSION TIME [CAR] | replay_live
          json TYPE {params}               (any type with raw JSON params)
        """;

    public static (string Type, JsonElement Params)? Parse(string line, out string? error)
    {
        error = null;
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            error = "empty";
            return null;
        }

        var type = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : "";
        var args = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var p = new JsonObject();

        string Tail(int skip) => string.Join(' ', args.Skip(skip));

        switch (type)
        {
            case "yellow":
                if (rest.Length > 0)
                {
                    p["message"] = rest;
                }

                break;
            case "pit_close" or "pit_open" or "restart_single" or "restart_double" or "clear_all":
                break;
            case "clear":
                p["car"] = args.ElementAtOrDefault(0);
                break;
            case "pace_laps_set" or "pace_laps_add":
                p["n"] = Number(args.ElementAtOrDefault(0));
                break;
            case "black_flag":
                p["car"] = args.ElementAtOrDefault(0);
                p["mode"] = args.ElementAtOrDefault(1);
                if (args.Length > 2)
                {
                    p["seconds"] = Number(args[2]);
                }

                break;
            case "wave_by" or "eol" or "dq" or "remove":
                p["car"] = args.ElementAtOrDefault(0);
                if (args.Length > 1)
                {
                    p["message"] = Tail(1);
                }

                break;
            case "announce":
                p["text"] = rest;
                break;
            case "replay_jump":
                p["sessionNum"] = Number(args.ElementAtOrDefault(0));
                p["sessionTime"] = Number(args.ElementAtOrDefault(1));
                if (args.Length > 2)
                {
                    p["car"] = args[2];
                }

                break;
            case "replay_live":
                break;
            case "json":
                var raw = rest.Split(' ', 2);
                if (raw.Length < 2)
                {
                    error = "usage: json TYPE {params}";
                    return null;
                }

                try
                {
                    return (raw[0], JsonDocument.Parse(raw[1]).RootElement.Clone());
                }
                catch (JsonException e)
                {
                    error = "invalid JSON: " + e.Message;
                    return null;
                }

            default:
                error = $"unknown command '{type}'";
                return null;
        }

        return (type, JsonDocument.Parse(p.ToJsonString()).RootElement.Clone());
    }

    /// <summary>Whole numbers as integers, other numbers as decimals, anything else as the text (the renderer rejects it).</summary>
    private static JsonNode? Number(string? s) =>
        s is null ? null
        : long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n)
        : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d)
        : JsonValue.Create(s);
}
