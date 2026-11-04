namespace PaceLaps.Agent.Core.Time;

/// <summary>
/// Estimates server clock minus local clock from ping/pong round trips. The sample with the shortest round trip
/// among the recent ones wins, because network delay is the main error and it is smallest there. With the
/// server's receive time the NTP formula takes the server's processing time out (spec-feedback item 18); without
/// it the server is assumed to stamp its time halfway through the round trip.
/// </summary>
public sealed class ClockOffsetEstimator
{
    private readonly int _window;
    private readonly LinkedList<(TimeSpan Offset, TimeSpan RoundTrip)> _samples = new();
    private readonly object _gate = new();

    public ClockOffsetEstimator(int window = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);
        _window = window;
    }

    public bool HasEstimate
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count > 0;
            }
        }
    }

    /// <summary>Server time minus local time. Zero until the first sample.</summary>
    public TimeSpan Offset => Best?.Offset ?? TimeSpan.Zero;

    public TimeSpan? RoundTrip => Best?.RoundTrip;

    private (TimeSpan Offset, TimeSpan RoundTrip)? Best
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count == 0 ? null : _samples.MinBy(s => s.RoundTrip);
            }
        }
    }

    /// <param name="sentAt">Local time the ping was sent.</param>
    /// <param name="serverTime">Server time in the pong.</param>
    /// <param name="receivedAt">Local time the pong arrived.</param>
    public void Add(DateTimeOffset sentAt, DateTimeOffset serverTime, DateTimeOffset receivedAt) => Add(sentAt, null, serverTime, receivedAt);

    /// <param name="sentAt">Local time the ping was sent.</param>
    /// <param name="serverReceivedAt">Server time the ping arrived, if the pong carries it.</param>
    /// <param name="serverTime">Server time the pong left.</param>
    /// <param name="receivedAt">Local time the pong arrived.</param>
    public void Add(DateTimeOffset sentAt, DateTimeOffset? serverReceivedAt, DateTimeOffset serverTime, DateTimeOffset receivedAt)
    {
        var processing = serverReceivedAt is { } r && r <= serverTime ? serverTime - r : TimeSpan.Zero;
        var roundTrip = receivedAt - sentAt - processing;
        if (roundTrip < TimeSpan.Zero)
        {
            return;
        }

        var offset = serverReceivedAt is { } received && received <= serverTime
            ? ((received - sentAt) + (serverTime - receivedAt)) / 2
            : serverTime - (sentAt + roundTrip / 2);
        lock (_gate)
        {
            _samples.AddLast((offset, roundTrip));
            while (_samples.Count > _window)
            {
                _samples.RemoveFirst();
            }
        }
    }
}
