using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// Local rolling log file for every command and its result. No tokens or secrets are ever passed to the logger.
/// </summary>
public static class AgentLogging
{
    public static string LogDirectory { get; } = AgentPaths.File("logs");

    /// <param name="recent">Also keeps the last lines in memory (the tray app's status window shows them; no debug lines).</param>
    public static ILoggerFactory Create(RecentLog? recent = null)
    {
        Directory.CreateDirectory(LogDirectory);
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogDirectory, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
        if (recent is not null)
        {
            configuration = configuration.WriteTo.Sink(recent, LogEventLevel.Information);
        }

        return new SerilogLoggerFactory(configuration.CreateLogger(), dispose: true);
    }
}
