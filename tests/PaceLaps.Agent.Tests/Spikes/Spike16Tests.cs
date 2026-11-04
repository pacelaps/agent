using PaceLaps.Agent.ConsoleHost.Spikes;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.Tests.Spikes;

public sealed class Spike16Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static MonitorEvent Broadcast(int ms, string description) =>
        new("sdk_broadcast", T0.AddMilliseconds(ms), new { message = "ChatCommand", description });

    [Fact]
    public void Times_each_begin_chat_to_the_next_cancel_chat()
    {
        var gaps = Spike16Background.ChatGaps([
            Broadcast(1000, "chat Cancel"),          // no begin before it: ignored
            Broadcast(2000, "chat BeginChat"),
            Broadcast(2012, "chat Cancel"),
            Broadcast(5000, "chat BeginChat"),
            Broadcast(5100, "TelemCommand 1 0"),
            Broadcast(5300, "chat Cancel"),
            Broadcast(9000, "chat BeginChat"),       // never cancelled: no gap
        ]);

        Assert.Equal([12L, 300L], gaps);
    }
}
