using PaceLaps.Agent.Core.Time;

namespace PaceLaps.Agent.Tests.Fakes;

/// <summary>Time moves only when code awaits <see cref="Delay"/> or a test calls <see cref="Advance"/>.</summary>
internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 7, 19, 0, 0, TimeSpan.Zero);

    public TimeSpan Elapsed { get; private set; }

    public void Advance(TimeSpan by)
    {
        Elapsed += by;
        UtcNow += by;
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay > TimeSpan.Zero)
        {
            Advance(delay);
        }

        return Task.CompletedTask;
    }
}
