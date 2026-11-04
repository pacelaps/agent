namespace PaceLaps.Agent.Core.Time;

/// <summary>
/// The only source of time in the agent. Tests use a fake that advances on <see cref="Delay"/>.
/// </summary>
public interface IClock
{
    /// <summary>Wall-clock time in UTC, for timestamps in logs and messages.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Monotonic time since an arbitrary start, for measuring durations.</summary>
    TimeSpan Elapsed { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken = default);
}
