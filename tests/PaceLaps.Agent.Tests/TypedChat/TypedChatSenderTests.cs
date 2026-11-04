using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.TypedChat;

public sealed class TypedChatSenderTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;

    public TypedChatSenderTests()
    {
        _desktop = new FakeDesktop(_clock);
    }

    private TypedChatSender Sender(TypedChatOptions? options = null) =>
        new(_desktop, _desktop, _desktop, _clock, options);

    [Fact]
    public async Task Sends_in_order_and_restores_the_previous_window()
    {
        var result = await Sender().SendAsync("!yellow", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(["front:sim", "begin", "keys:7", "enter", "front:browser"], _desktop.Calls);
        Assert.Equal("!yellow", _desktop.Typed);
        Assert.False(_desktop.ChatOpen);
        Assert.Equal(FakeDesktop.Browser, _desktop.Foreground);
        Assert.True(result.FocusRestored);
        Assert.False(result.ChatCancelled);
        Assert.Equal("browser.exe", result.PreviousProcess);
    }

    [Fact]
    public async Task Reports_timing_per_phase_from_the_clock()
    {
        var options = new TypedChatOptions
        {
            ChatOpenDelay = TimeSpan.FromMilliseconds(150),
            AfterEnterDelay = TimeSpan.FromMilliseconds(100),
        };

        var result = await Sender(options).SendAsync("abcd", TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMilliseconds(20), result.FocusTime);
        Assert.Equal(TimeSpan.FromMilliseconds(150), result.ChatOpenTime);
        Assert.Equal(TimeSpan.FromMilliseconds(104), result.TypingTime);
        Assert.Equal(TimeSpan.FromMilliseconds(20), result.RestoreTime);
        Assert.Equal(TimeSpan.FromMilliseconds(294), result.TotalTime);
    }

    [Fact]
    public async Task Pauses_before_Enter_when_configured()
    {
        var options = new TypedChatOptions { ChatOpenDelay = TimeSpan.Zero, AfterEnterDelay = TimeSpan.Zero, BeforeEnterDelay = TimeSpan.FromMilliseconds(300) };

        var result = await Sender(options).SendAsync("ab", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(TimeSpan.FromMilliseconds(302), result.TypingTime);
    }

    [Fact]
    public async Task Does_not_touch_focus_when_iRacing_is_already_in_front()
    {
        _desktop.Foreground = FakeDesktop.Sim;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(result.SimWasForeground);
        Assert.Equal(["begin", "keys:2", "enter"], _desktop.Calls);
        Assert.Equal(FakeDesktop.Sim, _desktop.Foreground);
        Assert.True(result.FocusRestored);
    }

    [Fact]
    public async Task Rejects_unmappable_text_before_touching_focus_or_chat()
    {
        var result = await Sender().SendAsync("line\nbreak", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(TypedChatFailure.Unmappable, result.Failure);
        Assert.Contains("U+000A", result.Detail);
        Assert.Empty(_desktop.Calls);
    }

    [Fact]
    public async Task Fails_when_the_sim_window_is_missing()
    {
        _desktop.SimRunning = false;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.SimNotFound, result.Failure);
        Assert.Empty(_desktop.Calls);
    }

    [Fact]
    public async Task Fails_without_opening_chat_when_iRacing_will_not_come_forward()
    {
        _desktop.SimRefusesFocus = true;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.FocusFailed, result.Failure);
        Assert.DoesNotContain("begin", _desktop.Calls);
        Assert.DoesNotContain("cancel", _desktop.Calls);
        Assert.True(result.FocusRestored);
        Assert.Equal(FakeDesktop.Browser, _desktop.Foreground);
    }

    [Fact]
    public async Task Cancels_chat_and_restores_focus_when_keys_are_rejected()
    {
        _desktop.RejectKeys = true;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.KeyboardRejected, result.Failure);
        Assert.True(result.ChatCancelled);
        Assert.False(_desktop.ChatOpen);
        Assert.Equal(["front:sim", "begin", "keys:2", "cancel", "front:browser"], _desktop.Calls);
        Assert.True(result.FocusRestored);
    }

    [Fact]
    public async Task Cancels_chat_when_Enter_is_rejected()
    {
        _desktop.RejectEnter = true;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.KeyboardRejected, result.Failure);
        Assert.True(result.ChatCancelled);
        Assert.False(_desktop.ChatOpen);
    }

    [Fact]
    public async Task Cancels_chat_and_restores_focus_on_an_unexpected_exception()
    {
        _desktop.ThrowOnSend = new InvalidOperationException("boom");

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.Error, result.Failure);
        Assert.Contains("boom", result.Detail);
        Assert.True(result.ChatCancelled);
        Assert.Equal(FakeDesktop.Browser, _desktop.Foreground);
    }

    [Fact]
    public async Task Stops_typing_and_cancels_chat_when_focus_is_stolen_between_characters()
    {
        _desktop.StealFocusAfterChars = 2;
        var options = new TypedChatOptions { InterKeyDelay = TimeSpan.FromMilliseconds(5) };

        var result = await Sender(options).SendAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.FocusLost, result.Failure);
        Assert.Equal("he", _desktop.Typed);
        Assert.True(result.ChatCancelled);
        Assert.DoesNotContain("enter", _desktop.Calls);
    }

    [Fact]
    public async Task Does_not_press_Enter_when_focus_was_lost_during_a_bulk_send()
    {
        _desktop.StealFocusAfterChars = 1;

        var result = await Sender().SendAsync("hello", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.FocusLost, result.Failure);
        Assert.DoesNotContain("enter", _desktop.Calls);
        Assert.True(result.ChatCancelled);
    }

    [Fact]
    public async Task Reports_focus_not_restored_when_the_previous_window_refuses()
    {
        _desktop.BrowserRefusesFocus = true;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.FocusRestored);
    }

    [Fact]
    public async Task Reports_focus_not_restored_when_the_previous_window_is_gone()
    {
        _desktop.BrowserClosed = true;

        var result = await Sender().SendAsync("hi", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.False(result.FocusRestored);
        Assert.DoesNotContain("front:browser", _desktop.Calls);
    }

    [Fact]
    public async Task Cancellation_during_typing_cancels_chat_and_restores_focus()
    {
        using var cts = new CancellationTokenSource();
        var options = new TypedChatOptions { InterKeyDelay = TimeSpan.FromMilliseconds(5) };

        // Cancel the token from inside the keyboard fake after the first character.
        var keyboard = new CancellingKeyboard(_desktop, cts, afterStrokes: 1);
        var sender = new TypedChatSender(_desktop, keyboard, _desktop, _clock, options);

        var result = await sender.SendAsync("hello", cts.Token);

        Assert.Equal(TypedChatFailure.Cancelled, result.Failure);
        Assert.True(result.ChatCancelled);
        Assert.False(_desktop.ChatOpen);
        Assert.Equal(FakeDesktop.Browser, _desktop.Foreground);
    }

    private sealed class CancellingKeyboard(FakeDesktop inner, CancellationTokenSource cts, int afterStrokes) : IKeyboard
    {
        private int _sent;

        public bool TryMap(string text, out IReadOnlyList<KeyStroke> strokes, out char unmappable) =>
            inner.TryMap(text, out strokes, out unmappable);

        public bool Send(IReadOnlyList<KeyStroke> strokes)
        {
            _sent += strokes.Count;
            if (_sent >= afterStrokes)
            {
                cts.Cancel();
            }

            return inner.Send(strokes);
        }

        public bool SendEnter() => inner.SendEnter();
    }
}
