using System.Globalization;

namespace PaceLaps.Agent.ConsoleHost.Cli;

internal sealed class CliException(string message) : Exception(message);

/// <summary>Minimal parser: <c>command --name value --flag</c>. No dependency needed for a handful of options.</summary>
internal sealed class CliArgs
{
    private readonly Dictionary<string, string?> _options;

    private CliArgs(string command, Dictionary<string, string?> options)
    {
        Command = command;
        _options = options;
    }

    public string Command { get; }

    public static CliArgs Parse(string[] args)
    {
        var hasCommand = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal);
        var command = hasCommand ? args[0].ToLowerInvariant() : "help";
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = hasCommand ? 1 : 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliException($"unexpected argument '{a}'");
            }

            var name = a[2..];
            string? value = null;
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            options[name] = value;
        }

        return new CliArgs(command, options);
    }

    public bool Has(string name) => _options.ContainsKey(name);

    /// <summary>The option's value, or null if it was not given. An option given without a value is an error, not a default.</summary>
    public string? Get(string name)
    {
        if (!_options.TryGetValue(name, out var v))
        {
            return null;
        }

        return v ?? throw new CliException($"--{name} needs a value");
    }

    public string Require(string name) =>
        Get(name) ?? throw new CliException($"--{name} is required");

    public int GetInt(string name, int fallback, int min, int max)
    {
        var raw = Get(name);
        if (raw is null)
        {
            return fallback;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
        {
            throw new CliException($"--{name} must be a whole number from {min} to {max}");
        }

        return v;
    }

    public TimeSpan GetMs(string name, TimeSpan fallback, int maxMs = 60_000) =>
        TimeSpan.FromMilliseconds(GetInt(name, (int)fallback.TotalMilliseconds, 0, maxMs));

    public TimeSpan GetSeconds(string name, TimeSpan fallback, int maxSeconds = 3600) =>
        TimeSpan.FromSeconds(GetInt(name, (int)fallback.TotalSeconds, 0, maxSeconds));

    public string GetChoice(string name, params string[] choices)
    {
        var v = Require(name).ToLowerInvariant();
        return choices.Contains(v) ? v : throw new CliException($"--{name} must be one of: {string.Join(", ", choices)}");
    }
}
