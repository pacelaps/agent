using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Server;

namespace PaceLaps.Agent.Tests.Fakes;

/// <summary>A server that answers pings with a fixed offset and records everything the agent sends.</summary>
internal sealed class FakeServer(FakeClock clock) : IServerLink
{
    public TimeSpan ClockOffset { get; set; } = TimeSpan.FromMilliseconds(120);
    public TimeSpan RoundTrip { get; set; } = TimeSpan.FromMilliseconds(40);
    public bool AnswerPings { get; set; } = true;
    public SessionCheck Check { get; set; } = new(true, true);
    public ServerView View { get; set; } = new(true, "s_1", "Race 1", 1, true);
    public List<HelloMessage> Hellos { get; } = [];
    public List<object> Sent { get; } = [];
    public List<SessionIdentity> Checked { get; } = [];

    public OutageWindow? LastOutage { get; set; }

    /// <summary>What GET local-failsafe answers; FetchCount counts the requests.</summary>
    public LocalFailsafeDocumentMessage? LocalFailsafeDocument { get; set; }

    public int FetchCount { get; private set; }

    public Task<LocalFailsafeDocumentMessage?> FetchLocalFailsafeAsync(CancellationToken ct)
    {
        FetchCount++;
        return Task.FromResult(LocalFailsafeDocument);
    }

    public event Action<CommandMessage>? CommandReceived;

    public event Action<ServerView>? ViewReceived;

    public event Action<long>? EventsAcknowledged;

    public event Action? Reconnected;

    public void Deliver(CommandMessage command) => CommandReceived?.Invoke(command);

    /// <summary>A heartbeat result or executor assignment from the server.</summary>
    public void Push(ServerView view) => ViewReceived?.Invoke(view);

    public void Acknowledge(long seq) => EventsAcknowledged?.Invoke(seq);

    public void Reconnect() => Reconnected?.Invoke();

    public IEnumerable<T> SentOf<T>() => Sent.OfType<T>();

    public Task<ClockPongMessage?> PingAsync(ClockPingMessage ping, CancellationToken ct)
    {
        if (!AnswerPings)
        {
            return Task.FromResult<ClockPongMessage?>(null);
        }

        clock.Advance(RoundTrip / 2);
        var pong = new ClockPongMessage { ClientSentAt = ping.ClientSentAt, ServerTime = clock.UtcNow + ClockOffset };
        clock.Advance(RoundTrip / 2);
        return Task.FromResult<ClockPongMessage?>(pong);
    }

    public Task<SessionCheck> CheckSessionAsync(SessionIdentity identity, int operatorCustId, CancellationToken ct)
    {
        Checked.Add(identity);
        return Task.FromResult(Check);
    }

    public Task<ServerView> HelloAsync(HelloMessage hello, CancellationToken ct)
    {
        Hellos.Add(hello);
        Sent.Add(hello);
        return Task.FromResult(View);
    }

    public void Send(object message) => Sent.Add(message);
}

/// <summary>Answers operator questions from a list; records the questions.</summary>
internal sealed class FakePrompt(params bool[] answers) : IOperatorPrompt
{
    private int _next;

    public List<string> Questions { get; } = [];

    public Task<bool> ConfirmAsync(string question, CancellationToken ct)
    {
        Questions.Add(question);
        return Task.FromResult(_next < answers.Length ? answers[_next++] : true);
    }
}
