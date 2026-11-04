using System.Globalization;
using Serilog.Core;
using Serilog.Events;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// The last log lines in memory, for the tray app's status window (the files keep everything). The same lines as the
/// file, so no secrets either: nothing logs them.
/// </summary>
public sealed class RecentLog(int capacity = RecentLog.DefaultCapacity) : ILogEventSink
{
    public const int DefaultCapacity = 500;

    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private long _version;

    /// <summary>Goes up with every line, so a window redraws only when there is something new.</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Emit(LogEvent logEvent)
    {
        var line = $"{logEvent.Timestamp.LocalDateTime:HH:mm:ss} {Level(logEvent.Level)} {logEvent.RenderMessage(CultureInfo.InvariantCulture)}";
        if (logEvent.Exception is { } e)
        {
            line += $" ({e.GetType().Name}: {e.Message})";
        }

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }

            Interlocked.Increment(ref _version);
        }
    }

    public IReadOnlyList<string> Lines()
    {
        lock (_gate)
        {
            return [.. _lines];
        }
    }

    private static string Level(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => "WARN ",
        LogEventLevel.Error or LogEventLevel.Fatal => "ERROR",
        LogEventLevel.Debug or LogEventLevel.Verbose => "debug",
        _ => "info ",
    };
}
