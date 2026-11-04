using System.Diagnostics;

namespace PaceLaps.Agent.Core.Time;

public sealed class SystemClock : IClock
{
    private readonly long _start = Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_start);

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}
