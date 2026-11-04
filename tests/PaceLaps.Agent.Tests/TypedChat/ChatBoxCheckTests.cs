using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.Tests.Fakes;

namespace PaceLaps.Agent.Tests.TypedChat;

/// <summary>The chat box check after every typed line (spike 15, after the acceptance run's lost Enter).</summary>
public sealed class ChatBoxCheckTests
{
    private readonly FakeClock _clock = new();
    private readonly FakeDesktop _desktop;

    public ChatBoxCheckTests()
    {
        _desktop = new FakeDesktop(_clock);
    }

    private Task<TypedChatResult> Send(string text, bool withCheck = true) =>
        new TypedChatSender(_desktop, _desktop, _desktop, _clock, chatBox: withCheck ? _desktop : null)
            .SendAsync(text, TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_empty_box_after_Enter_means_sent()
    {
        var result = await Send("!eol #7");

        Assert.True(result.Success);
        Assert.Equal(ChatBoxCheck.Empty, result.ChatBox);
        Assert.Equal(["!eol #7"], _desktop.SentLines);
        Assert.Equal(["front:sim", "begin", "keys:7", "enter", "peek", "cancel", "front:browser"], _desktop.Calls);
        Assert.False(_desktop.ChatOpen);
        Assert.Null(result.Note);
        Assert.Equal(TimeSpan.FromMilliseconds(300), result.ChatCheckTime);
    }

    [Fact]
    public async Task A_line_left_in_the_box_gets_one_more_Enter()
    {
        _desktop.IgnoreEnters = 1;

        var result = await Send("!eol #7");

        Assert.True(result.Success);
        Assert.Equal(ChatBoxCheck.SentOnRetry, result.ChatBox);
        Assert.Equal(["!eol #7"], _desktop.SentLines);
        Assert.Equal(["front:sim", "begin", "keys:7", "enter", "peek", "enter", "peek", "cancel", "front:browser"], _desktop.Calls);
        Assert.Contains("second Enter sent the line", result.Note, StringComparison.Ordinal);
        Assert.False(_desktop.ChatOpen);
    }

    [Fact]
    public async Task A_line_still_there_after_the_second_Enter_is_cleared_and_fails()
    {
        _desktop.IgnoreEnters = 2;

        var result = await Send("!eol #7");

        Assert.False(result.Success);
        Assert.Equal(TypedChatFailure.LineNotSent, result.Failure);
        Assert.Equal(ChatBoxCheck.NotSent, result.ChatBox);
        Assert.Contains("'!eol #7'", result.Detail, StringComparison.Ordinal);
        Assert.Empty(_desktop.SentLines);
        Assert.Equal("", _desktop.Line);
        Assert.False(_desktop.ChatOpen);
        Assert.True(result.ChatCancelled);
        Assert.True(result.FocusRestored);
    }

    [Fact]
    public async Task Other_text_in_the_box_is_cleared_and_noted_but_the_line_counts_as_sent()
    {
        _desktop.BoxTextAfterEnter = "gg everyone";

        var result = await Send("!pitclose");

        Assert.True(result.Success);
        Assert.Equal(ChatBoxCheck.UnexpectedText, result.ChatBox);
        Assert.Contains("'gg everyone'", result.Note, StringComparison.Ordinal);
        Assert.Equal(["!pitclose"], _desktop.SentLines);
        Assert.Equal("", _desktop.Line);
        Assert.False(_desktop.ChatOpen);
    }

    [Fact]
    public async Task Our_line_mixed_with_other_text_is_not_pressed_again_but_cleared_and_fails()
    {
        _desktop.Line = "hel";
        _desktop.IgnoreEnters = 1;

        var result = await Send("!pitclose");

        Assert.False(result.Success);
        Assert.Equal(TypedChatFailure.UnexpectedChatText, result.Failure);
        Assert.Contains("'hel!pitclose'", result.Detail, StringComparison.Ordinal);
        Assert.Empty(_desktop.SentLines);
        Assert.Single(_desktop.Calls, c => c == "enter");
        Assert.Equal("", _desktop.Line);
    }

    [Fact]
    public async Task An_unreadable_box_is_cancelled_as_a_precaution_and_noted()
    {
        _desktop.PeekUnreadable = true;

        var result = await Send("!pitopen");

        Assert.True(result.Success);
        Assert.Equal(ChatBoxCheck.Unreadable, result.ChatBox);
        Assert.Contains("clipboard busy", result.Note, StringComparison.Ordinal);
        Assert.Contains("cancel", _desktop.Calls);
        Assert.True(result.ChatCancelled);
    }

    [Fact]
    public async Task Without_a_reader_nothing_changes()
    {
        _desktop.IgnoreEnters = 1;

        var result = await Send("!eol #7", withCheck: false);

        // The old behaviour, which the acceptance run caught: the sender thinks it sent the line.
        Assert.True(result.Success);
        Assert.Equal(ChatBoxCheck.Off, result.ChatBox);
        Assert.Empty(_desktop.SentLines);
        Assert.DoesNotContain("peek", _desktop.Calls);
    }
}
