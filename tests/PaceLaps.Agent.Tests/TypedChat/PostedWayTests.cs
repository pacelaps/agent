using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.TypedChat;

/// <summary>The typed channel without the focus switch (spike 16, spec-feedback 37).</summary>
public sealed class PostedWayTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;

    public PostedWayTests()
    {
        _desktop = new FakeDesktop(_clock);
    }

    private TypedChatSender Sender(bool withPoster = true) =>
        new(_desktop, _desktop, _desktop, _clock, chatBox: _desktop, poster: withPoster ? _desktop : null);

    [Fact]
    public async Task A_line_goes_begin_post_enter_cancel_and_never_touches_the_focus()
    {
        var start = _clock.Elapsed;

        var result = await Sender().SendAsync("!black #12 D", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(TypedChatWay.Posted, result.Way);
        Assert.Equal(["begin", "post:12", "post-enter", "cancel"], _desktop.Calls);
        Assert.Equal(["!black #12 D"], _desktop.SentLines);
        Assert.Equal(FakeDesktop.Browser, _desktop.Foreground);
        Assert.False(result.SimWasForeground);
        Assert.Equal("browser.exe", result.PreviousProcess);
        Assert.True(result.ChatCancelled);
        Assert.False(_desktop.ChatOpen);
        Assert.Equal(ChatBoxCheck.Off, result.ChatBox);
        // 50 ms after begin chat, 100 ms before cancel chat.
        Assert.Equal(TimeSpan.FromMilliseconds(150), _clock.Elapsed - start);
    }

    [Fact]
    public async Task Any_character_goes_as_it_is_without_a_keyboard_mapping()
    {
        // The fake keyboard cannot type '^'; the posted way does not need it to.
        var result = await Sender().SendAsync("/rc äöü ^ @", TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(["/rc äöü ^ @"], _desktop.SentLines);
    }

    [Fact]
    public async Task A_refused_post_fails_and_still_cancels_chat()
    {
        _desktop.RejectPosts = true;

        var result = await Sender().SendAsync("!pitclose", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(TypedChatFailure.PostRejected, result.Failure);
        Assert.Contains("elevated", result.Detail, StringComparison.Ordinal);
        Assert.Equal(["begin", "post:9", "cancel"], _desktop.Calls);
        Assert.False(_desktop.ChatOpen);
        Assert.True(result.ChatCancelled);
    }

    [Fact]
    public async Task A_lost_line_is_closed_by_cancel_chat_not_left_open()
    {
        _desktop.LosePostedLines = 1;

        var result = await Sender().SendAsync("/rc hello", TestContext.Current.CancellationToken);

        // The sender cannot know; telemetry decides for commands it can check.
        Assert.True(result.Success);
        Assert.Empty(_desktop.SentLines);
        Assert.False(_desktop.ChatOpen);
        Assert.Equal("", _desktop.Line);
    }

    [Fact]
    public async Task Without_the_sim_nothing_is_sent()
    {
        _desktop.SimRunning = false;

        var result = await Sender().SendAsync("!pitclose", TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatFailure.SimNotFound, result.Failure);
        Assert.Equal(TypedChatWay.Posted, result.Way);
        Assert.Empty(_desktop.Calls);
    }

    [Fact]
    public async Task The_setting_off_or_no_poster_types_the_focus_way_as_before()
    {
        var off = Sender();
        off.WithoutFocus = false;
        Assert.Equal(TypedChatWay.Focus, off.DefaultWay);
        var result = await off.SendAsync("!pitclose", TestContext.Current.CancellationToken);
        Assert.Equal(TypedChatWay.Focus, result.Way);
        Assert.Contains("front:sim", _desktop.Calls);
        Assert.DoesNotContain("post-enter", _desktop.Calls);

        Assert.Equal(TypedChatWay.Focus, Sender(withPoster: false).DefaultWay);
        Assert.Equal(TypedChatWay.Focus, (await Sender(withPoster: false).SendAsync("!pitopen", TypedChatWay.Posted, TestContext.Current.CancellationToken)).Way);
    }

    [Fact]
    public async Task The_focus_way_can_be_asked_for_with_the_poster_there()
    {
        var result = await Sender().SendAsync("!help", TypedChatWay.Focus, TestContext.Current.CancellationToken);

        Assert.Equal(TypedChatWay.Focus, result.Way);
        Assert.Equal(["front:sim", "begin", "keys:5", "enter", "peek", "cancel", "front:browser"], _desktop.Calls);
    }
}
