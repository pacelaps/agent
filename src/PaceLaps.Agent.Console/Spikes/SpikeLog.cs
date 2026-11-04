using System.Text.Json;
using System.Text.Json.Serialization;
using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.ConsoleHost.Spikes;

/// <summary>
/// Structured spike log: one JSON object per line (<c>{"t":…,"type":…,"data":{…}}</c>), flushed after every
/// line so an aborted run still leaves a usable file. No user names: car numbers only.
/// </summary>
internal sealed class SpikeLog : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly StreamWriter _writer;
    private readonly IClock _clock;

    private SpikeLog(string path, string runId, StreamWriter writer, IClock clock)
    {
        Path = path;
        RunId = runId;
        _writer = writer;
        _clock = clock;
    }

    public string Path { get; }

    public string RunId { get; }

    public static SpikeLog Create(string directory, int spike, IClock clock)
    {
        Directory.CreateDirectory(directory);
        var runId = Guid.NewGuid().ToString("N")[..4];
        // UTC: local time repeats an hour when daylight saving ends, which made names ambiguous (2026-10-25).
        var name = $"spike-{spike}-{clock.UtcNow.UtcDateTime:yyyyMMdd-HHmmss}Z-{runId}.jsonl";
        var path = System.IO.Path.Combine(directory, name);
        var writer = new StreamWriter(path, append: false, new System.Text.UTF8Encoding(false));
        return new SpikeLog(path, runId, writer, clock);
    }

    /// <summary>Thread-safe: background recorders and the prompt loop write to the same log.</summary>
    public void Write(string type, object data)
    {
        var line = JsonSerializer.Serialize(new { t = _clock.UtcNow.ToString("O"), type, data }, JsonOptions);
        lock (_writer)
        {
            _writer.WriteLine(line);
            _writer.Flush();
        }
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>
    /// The repository's <c>docs/spike-logs</c> if the program runs from inside the repository, else <c>./spike-logs</c>.
    /// </summary>
    public static string DefaultDirectory()
    {
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "docs", "spike-results.md")))
                {
                    return System.IO.Path.Combine(dir.FullName, "docs", "spike-logs");
                }
            }
        }

        return System.IO.Path.Combine(Environment.CurrentDirectory, "spike-logs");
    }
}
