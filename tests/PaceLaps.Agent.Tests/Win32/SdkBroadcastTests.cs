using System.Runtime.InteropServices;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.Tests.Win32;

public sealed class SdkBroadcastTests
{
    private static nint W(int msg, int var1) => (nint)((var1 << 16) | msg);

    [Theory]
    [InlineData(8, 0, 13, "chat macro 13 (slot 14)")]
    [InlineData(8, 1, 0, "chat BeginChat")]
    [InlineData(8, 3, 0, "chat Cancel")]
    [InlineData(9, 1, 0, "pit command 1 (0)")]
    [InlineData(6, 0, 0, "ReplaySetState 0 0")]
    [InlineData(42, 1, 2, "Unknown42 1 2")]
    public void DecodesTheSdksMessages(int msg, int var1, long var2, string expected)
    {
        var decoded = SdkBroadcast.Decode(W(msg, var1), (nint)var2);

        Assert.Equal(expected, decoded.Description);
        Assert.Equal(var1, decoded.Var1);
        Assert.Equal(var2, decoded.Var2);
    }

    [Fact]
    public async Task TheMonitorHearsABroadcastToEveryWindow()
    {
        using var monitor = new InputMonitor();
        await monitor.StartAsync();

        // Message 99 is not one the SDK defines: a running iRacing ignores it.
        var id = RegisterWindowMessage(SdkBroadcast.MessageName);
        Assert.True(SendNotifyMessage(HwndBroadcast, id, W(99, 7), 5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        MonitorEvent? seen = null;
        await foreach (var e in monitor.Events.ReadAllAsync(timeout.Token))
        {
            if (e.Kind == "sdk_broadcast")
            {
                seen = e;
                break;
            }
        }

        Assert.NotNull(seen);
        Assert.Contains("Unknown99 7 5", System.Text.Json.JsonSerializer.Serialize(seen!.Data));
    }

    [Theory]
    [InlineData(MessageDelivery.Post)]
    [InlineData(MessageDelivery.Send)]
    public void ReportsEveryMessageAWindowDidNotTake(MessageDelivery delivery)
    {
        // A handle no window has: every message fails, and the first error is kept for the log.
        var gone = (nint)0x7FFF0001;

        var text = WindowMessageTyping.PostText(gone, "/rc x", delivery);
        var enter = WindowMessageTyping.PostEnter(gone, PostedEnter.Key, delivery);

        Assert.Equal(new PostResult(0, 5, text.FirstError), text);
        Assert.NotNull(text.FirstError);
        Assert.Equal(2, enter.Failed);
    }

    private static readonly nint HwndBroadcast = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendNotifyMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}
