using PaceLaps.Agent.Core.Contracts;

namespace PaceLaps.Agent.Core.Events;

/// <summary>
/// Bounded buffer of sent events with sequence numbers (spec: Connection loss and resync). Events stay until the
/// server acknowledges them, so they can be replayed after a reconnect. When full, the oldest events are dropped
/// and counted, so a resync can report the gap.
/// </summary>
public sealed class EventBuffer
{
    private readonly int _capacity;
    private readonly LinkedList<AgentEventMessage> _events = new();
    private readonly object _gate = new();
    private long _nextSeq;

    /// <param name="firstSeq">
    /// The first sequence number. The agent passes its start time in Unix milliseconds: the server stores each agent
    /// and seq once, so numbers must keep increasing across restarts (agent-messages.schema.json event.seq).
    /// </param>
    public EventBuffer(int capacity = 1000, long firstSeq = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(firstSeq, 0);
        _capacity = capacity;
        _nextSeq = firstSeq;
    }

    /// <summary>Events lost because the buffer was full before the server acknowledged them.</summary>
    public long Dropped { get; private set; }

    public long LastAcked { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    public AgentEventMessage Add(string agentId, DateTimeOffset eventTime, DetectedEvent e)
    {
        lock (_gate)
        {
            var message = new AgentEventMessage { AgentId = agentId, Seq = _nextSeq++, EventTime = eventTime, EventType = e.Type, Data = e.Data };
            _events.AddLast(message);
            while (_events.Count > _capacity)
            {
                _events.RemoveFirst();
                Dropped++;
            }

            return message;
        }
    }

    /// <summary>The server has every event up to and including <paramref name="seq"/>.</summary>
    public void Acknowledge(long seq)
    {
        lock (_gate)
        {
            LastAcked = Math.Max(LastAcked, seq);
            while (_events.First is { } first && first.Value.Seq <= seq)
            {
                _events.RemoveFirst();
            }
        }
    }

    /// <summary>The events after the server's last acknowledged sequence number, oldest first.</summary>
    public IReadOnlyList<AgentEventMessage> After(long seq)
    {
        lock (_gate)
        {
            return _events.Where(e => e.Seq > seq).ToList();
        }
    }
}
