using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.IRacing.Sdk;

public enum ReadbackOutcome
{
    /// <summary>The chat box held this text (read through the clipboard).</summary>
    Text,

    /// <summary>The copy left the clipboard unchanged: the box was empty, or the sim does not support copying.</summary>
    NothingCopied,

    /// <summary>iRacing could not be brought to the front, or the clipboard was busy.</summary>
    Failed,
}

public sealed record ReadbackResult(ReadbackOutcome Outcome, string? Text, string? Detail = null);

/// <summary>
/// Reads what is in iRacing's chat box, then closes it (spike 15). The SDK cannot read chat, so this opens the
/// chat box (begin-chat keeps text already in it, if the sim works that way), selects all and copies with
/// Ctrl+A, Ctrl+C, reads the clipboard, cancels chat, and restores the operator's clipboard text and window.
/// A sentinel on the clipboard tells "nothing copied" apart from real text.
/// </summary>
public sealed class ChatReadback : IChatBoxReader
{
    public const string Sentinel = "⁣pacelaps-readback-sentinel⁣";

    private readonly Win32WindowFocus _focus;
    private readonly ScancodeKeyboard _keyboard;
    private readonly ISimChat _chat;
    private readonly IClock _clock;

    public ChatReadback(Win32WindowFocus focus, ScancodeKeyboard keyboard, ISimChat chat, IClock clock)
    {
        _focus = focus;
        _keyboard = keyboard;
        _chat = chat;
        _clock = clock;
    }

    public TimeSpan OpenDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan CopyDelay { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <param name="openChat">Send begin-chat first. False reads a chat box that is already open.</param>
    public async Task<ReadbackResult> ReadAndCloseAsync(bool openChat = true, CancellationToken ct = default)
    {
        var sim = _focus.FindSimWindow();
        if (sim.IsNone)
        {
            return new ReadbackResult(ReadbackOutcome.Failed, null, "iRacing window not found");
        }

        var previous = _focus.GetForeground();
        var saved = ClipboardText.Get();
        try
        {
            if (previous != sim)
            {
                _focus.BringToFront(sim);
                var until = _clock.Elapsed + TimeSpan.FromSeconds(1);
                while (_focus.GetForeground() != sim && _clock.Elapsed < until)
                {
                    await _clock.Delay(TimeSpan.FromMilliseconds(10), ct).ConfigureAwait(false);
                }

                if (_focus.GetForeground() != sim)
                {
                    return new ReadbackResult(ReadbackOutcome.Failed, null, "iRacing did not come to the front");
                }
            }

            if (!ClipboardText.Set(Sentinel))
            {
                return new ReadbackResult(ReadbackOutcome.Failed, null, "clipboard busy");
            }

            if (openChat)
            {
                _chat.BeginChat();
                await _clock.Delay(OpenDelay, ct).ConfigureAwait(false);
            }

            _keyboard.SendCtrlChord(ScancodeKeyboard.ScanA);
            await _clock.Delay(TimeSpan.FromMilliseconds(30), ct).ConfigureAwait(false);
            _keyboard.SendCtrlChord(ScancodeKeyboard.ScanC);
            await _clock.Delay(CopyDelay, ct).ConfigureAwait(false);
            var copied = ClipboardText.Get();
            return Interpret(copied);
        }
        finally
        {
            _chat.CancelChat();
            ClipboardText.Set(saved);
            if (previous != sim && !previous.IsNone)
            {
                _focus.BringToFront(previous);
            }
        }
    }

    /// <summary>
    /// The typed channel's chat box check: reads the box with iRacing already in front and leaves it open (the
    /// caller presses Enter again or cancels chat). Restores the operator's clipboard text.
    /// </summary>
    public async Task<ChatBoxContent> PeekAsync(CancellationToken ct)
    {
        var saved = ClipboardText.Get();
        try
        {
            if (!ClipboardText.Set(Sentinel))
            {
                return ChatBoxContent.Unreadable("clipboard busy");
            }

            _chat.BeginChat();
            await _clock.Delay(OpenDelay, ct).ConfigureAwait(false);
            _keyboard.SendCtrlChord(ScancodeKeyboard.ScanA);
            await _clock.Delay(TimeSpan.FromMilliseconds(30), ct).ConfigureAwait(false);
            _keyboard.SendCtrlChord(ScancodeKeyboard.ScanC);
            await _clock.Delay(CopyDelay, ct).ConfigureAwait(false);
            var r = Interpret(ClipboardText.Get());
            return r.Outcome == ReadbackOutcome.Text ? new ChatBoxContent(true, r.Text) : ChatBoxContent.Empty;
        }
        finally
        {
            ClipboardText.Set(saved);
        }
    }

    /// <summary>What the clipboard after the copy means.</summary>
    public static ReadbackResult Interpret(string? clipboardAfterCopy) =>
        clipboardAfterCopy == Sentinel || clipboardAfterCopy is null
            ? new ReadbackResult(ReadbackOutcome.NothingCopied, null)
            : new ReadbackResult(ReadbackOutcome.Text, clipboardAfterCopy);
}
