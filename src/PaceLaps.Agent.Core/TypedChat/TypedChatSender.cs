using PaceLaps.Agent.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.TypedChat;

public sealed record TypedChatOptions
{
    /// <summary>Wait after begin-chat before typing. Assumption until spike 2 measures it.</summary>
    public TimeSpan ChatOpenDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Pause between characters. Zero sends the whole text in one SendInput call.</summary>
    public TimeSpan InterKeyDelay { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Wait after Enter before focus moves away. SendInput only queues events; switching the foreground
    /// window too early could deliver the rest of the queue to the other window.
    /// </summary>
    public TimeSpan AfterEnterDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Pause between the last character and Enter. Zero by default; spike 15 tests whether a pause stops the sim
    /// from missing the Enter (acceptance run: "!eol #7" stayed in the chat box twice).
    /// </summary>
    public TimeSpan BeforeEnterDelay { get; init; } = TimeSpan.Zero;

    public TimeSpan FocusTimeout { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan FocusPollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// The posted way: wait after begin-chat before the text. Not needed for the order (the SDK's broadcasts are
    /// posted too); a margin, as in spike 16's form d1, which arrived in and out of the car.
    /// </summary>
    public TimeSpan PostedOpenDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The posted way: wait after Enter before cancel chat closes the box (spec-feedback 37: a 100 ms margin).</summary>
    public TimeSpan PostedCancelDelay { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>How a typed line reaches the sim (spec-feedback 37).</summary>
public enum TypedChatWay
{
    /// <summary>Begin chat, the text and Enter posted to the sim's window, cancel chat: iRacing stays where it is.</summary>
    Posted,

    /// <summary>iRacing to the front, scancode keystrokes, Enter, the chat box readback, the previous window back.</summary>
    Focus,
}

public enum TypedChatFailure
{
    None,
    Unmappable,
    SimNotFound,
    FocusFailed,
    FocusLost,
    KeyboardRejected,
    Cancelled,
    Error,

    /// <summary>The posted way: Windows refused a window message to the sim (is iRacing running elevated?).</summary>
    PostRejected,

    /// <summary>The line stayed in the chat box after a second Enter (chat box check); it was cleared.</summary>
    LineNotSent,

    /// <summary>The chat box held our line mixed with other text after Enter (chat box check); it was cleared.</summary>
    UnexpectedChatText,
}

/// <summary>
/// Outcome of one typed message. <see cref="Success"/> means the keystrokes and Enter went to the sim
/// window; the SDK cannot read chat back, so arrival in chat is not confirmed here.
/// </summary>
public sealed record TypedChatResult
{
    public required bool Success { get; init; }

    public TypedChatWay Way { get; init; } = TypedChatWay.Focus;
    public required TypedChatFailure Failure { get; init; }
    public string? Detail { get; init; }
    public required bool SimWasForeground { get; init; }
    public required string PreviousProcess { get; init; }
    public string? FocusMethod { get; init; }
    public required bool ChatCancelled { get; init; }

    /// <summary>True when the window that was in front before is in front again (or nothing needed restoring).</summary>
    public required bool FocusRestored { get; init; }

    public required TimeSpan FocusTime { get; init; }
    public required TimeSpan ChatOpenTime { get; init; }
    public required TimeSpan TypingTime { get; init; }
    public required TimeSpan RestoreTime { get; init; }
    public required TimeSpan TotalTime { get; init; }

    /// <summary>What the chat box check after Enter found (Off without a chat box reader).</summary>
    public ChatBoxCheck ChatBox { get; init; } = ChatBoxCheck.Off;

    /// <summary>A note on a successful send, for example that a second Enter was needed.</summary>
    public string? Note { get; init; }

    public TimeSpan ChatCheckTime { get; init; }
}

/// <summary>
/// The typed command channel (spec: Command channels; spec-feedback 37), in two ways. Posted (the default when a
/// poster is given): begin chat, the text and Enter posted to the sim's window, cancel chat; no focus change, and the
/// chat box cannot be read. Focus: remember the foreground window, bring iRacing forward, begin chat, type scancodes,
/// press Enter, read the chat box, cancel chat on any failure, restore the window. Both report timing. Sends run one
/// at a time.
/// </summary>
public sealed class TypedChatSender
{
    private readonly IWindowFocus _focus;
    private readonly IKeyboard _keyboard;
    private readonly ISimChat _chat;
    private readonly IClock _clock;
    private readonly TypedChatOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IChatBoxReader? _chatBox;
    private readonly IChatPoster? _poster;

    public TypedChatSender(IWindowFocus focus, IKeyboard keyboard, ISimChat chat, IClock clock,
        TypedChatOptions? options = null, ILogger<TypedChatSender>? logger = null, IChatBoxReader? chatBox = null,
        IChatPoster? poster = null)
    {
        _focus = focus;
        _keyboard = keyboard;
        _chat = chat;
        _clock = clock;
        _options = options ?? new TypedChatOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _chatBox = chatBox;
        _poster = poster;
    }

    public TypedChatOptions Options => _options;

    /// <summary>The operator's setting (typedWithoutFocus): off sends every line the focus way, as before.</summary>
    public bool WithoutFocus { get; set; } = true;

    /// <summary>The way a line goes unless the caller asks for one: posted when a poster exists and the setting is on.</summary>
    public TypedChatWay DefaultWay => _poster is not null && WithoutFocus ? TypedChatWay.Posted : TypedChatWay.Focus;

    public Task<TypedChatResult> SendAsync(string text, CancellationToken cancellationToken = default) =>
        SendAsync(text, DefaultWay, cancellationToken);

    /// <summary>Sends one line. <see cref="TypedChatWay.Posted"/> without a poster falls back to the focus way.</summary>
    public async Task<TypedChatResult> SendAsync(string text, TypedChatWay way, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = way == TypedChatWay.Posted && _poster is not null
                ? await SendPostedAsync(text, _poster, cancellationToken).ConfigureAwait(false)
                : await SendCoreAsync(text, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _logger.LogInformation("Typed chat sent {Text} the {Way} way in {TotalMs:F0} ms, focus restored {FocusRestored}, chat box {ChatBox}{Note}",
                    text, result.Way, result.TotalTime.TotalMilliseconds, result.FocusRestored, result.ChatBox, result.Note is null ? "" : ": " + result.Note);
            }
            else
            {
                _logger.LogWarning("Typed chat failed {Failure} ({Detail}) the {Way} way for {Text}, chat cancelled {ChatCancelled}, focus restored {FocusRestored}",
                    result.Failure, result.Detail, result.Way, text, result.ChatCancelled, result.FocusRestored);
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The posted way (spike 16): begin chat, the text and Enter posted to the sim's window, a margin, cancel chat.
    /// The SDK's broadcasts are posted messages too, so the sim handles them in this order. Cancel chat runs after
    /// every line: it closes a box whose Enter was lost (the line is then not sent, and telemetry tells), and is
    /// harmless after a sent line. The foreground window is never touched.
    /// </summary>
    private async Task<TypedChatResult> SendPostedAsync(string text, IChatPoster poster, CancellationToken ct)
    {
        var start = _clock.Elapsed;
        var sim = _focus.FindSimWindow();
        if (sim.IsNone)
        {
            return Early(TypedChatFailure.SimNotFound, "iRacing window not found", start) with { Way = TypedChatWay.Posted };
        }

        var foreground = _focus.GetForeground();
        var failure = TypedChatFailure.None;
        string? detail = null;
        var chatOpen = false;
        TimeSpan openTime = TimeSpan.Zero, typingTime = TimeSpan.Zero;
        try
        {
            ct.ThrowIfCancellationRequested();
            var t = _clock.Elapsed;
            chatOpen = true;
            _chat.BeginChat();
            if (_options.PostedOpenDelay > TimeSpan.Zero)
            {
                await _clock.Delay(_options.PostedOpenDelay, ct).ConfigureAwait(false);
            }

            openTime = _clock.Elapsed - t;
            t = _clock.Elapsed;
            if (!poster.PostText(sim, text))
            {
                (failure, detail) = (TypedChatFailure.PostRejected, "Windows refused the text for the iRacing window (is iRacing running elevated?)");
            }
            else if (!poster.PostEnter(sim))
            {
                (failure, detail) = (TypedChatFailure.PostRejected, "Windows refused the Enter for the iRacing window (is iRacing running elevated?)");
            }
            else
            {
                await _clock.Delay(_options.PostedCancelDelay, CancellationToken.None).ConfigureAwait(false);
            }

            typingTime = _clock.Elapsed - t;
        }
        catch (OperationCanceledException)
        {
            (failure, detail) = (TypedChatFailure.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            (failure, detail) = (TypedChatFailure.Error, ex.GetType().Name + ": " + ex.Message);
        }

        var chatCancelled = false;
        if (chatOpen)
        {
            try
            {
                _chat.CancelChat();
                chatCancelled = true;
            }
            catch (Exception ex)
            {
                detail = (detail is null ? "" : detail + "; ") + $"cancel chat failed: {ex.Message}";
            }
        }

        return new TypedChatResult
        {
            Success = failure == TypedChatFailure.None,
            Way = TypedChatWay.Posted,
            Failure = failure,
            Detail = detail,
            SimWasForeground = foreground == sim,
            PreviousProcess = foreground.IsNone ? "(none)" : _focus.DescribeProcess(foreground),
            ChatCancelled = chatCancelled,
            FocusRestored = true,
            FocusTime = TimeSpan.Zero,
            ChatOpenTime = openTime,
            TypingTime = typingTime,
            RestoreTime = TimeSpan.Zero,
            TotalTime = _clock.Elapsed - start,
        };
    }

    private async Task<TypedChatResult> SendCoreAsync(string text, CancellationToken ct)
    {
        var start = _clock.Elapsed;
        var attempt = new Attempt();

        if (!_keyboard.TryMap(text, out var strokes, out var bad))
        {
            return Early(TypedChatFailure.Unmappable, $"character U+{(int)bad:X4} cannot be typed", start);
        }

        var sim = _focus.FindSimWindow();
        if (sim.IsNone)
        {
            return Early(TypedChatFailure.SimNotFound, "iRacing window not found", start);
        }

        var previous = _focus.GetForeground();
        var simWasForeground = previous == sim;
        var previousProcess = previous.IsNone ? "(none)" : _focus.DescribeProcess(previous);

        try
        {
            await RunAsync(attempt, text, strokes, sim, simWasForeground, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            attempt.Fail(TypedChatFailure.Cancelled, "cancelled");
        }
        catch (Exception ex)
        {
            attempt.Fail(TypedChatFailure.Error, ex.GetType().Name + ": " + ex.Message);
        }

        // Never leave chat open. Cancel works through the SDK without focus.
        var chatCancelled = false;
        if (attempt.ChatOpen)
        {
            try
            {
                _chat.CancelChat();
                chatCancelled = true;
            }
            catch (Exception ex)
            {
                attempt.Detail += $"; cancel chat failed: {ex.Message}";
            }
        }

        // Restore the previous window, also after a failure. Not cancellable on purpose.
        var restoreStart = _clock.Elapsed;
        var restored = true;
        if (!simWasForeground && !previous.IsNone)
        {
            if (!_focus.IsWindow(previous))
            {
                restored = false;
            }
            else if (_focus.GetForeground() != previous)
            {
                _focus.BringToFront(previous);
                restored = await WaitForForegroundAsync(previous, CancellationToken.None).ConfigureAwait(false);
            }
        }

        var restoreTime = _clock.Elapsed - restoreStart;

        return new TypedChatResult
        {
            Success = attempt.Failure == TypedChatFailure.None,
            Failure = attempt.Failure,
            Detail = attempt.Detail,
            SimWasForeground = simWasForeground,
            PreviousProcess = previousProcess,
            FocusMethod = attempt.FocusMethod,
            ChatCancelled = chatCancelled,
            FocusRestored = restored,
            FocusTime = attempt.FocusTime,
            ChatOpenTime = attempt.ChatOpenTime,
            TypingTime = attempt.TypingTime,
            RestoreTime = restoreTime,
            TotalTime = _clock.Elapsed - start,
            ChatBox = attempt.ChatBox,
            Note = attempt.Note,
            ChatCheckTime = attempt.ChatCheckTime,
        };
    }

    private async Task RunAsync(Attempt attempt, string text, IReadOnlyList<KeyStroke> strokes, WindowHandle sim,
        bool simWasForeground, CancellationToken ct)
    {
        var t = _clock.Elapsed;
        if (!simWasForeground)
        {
            attempt.FocusMethod = _focus.BringToFront(sim);
            if (!await WaitForForegroundAsync(sim, ct).ConfigureAwait(false))
            {
                attempt.Fail(TypedChatFailure.FocusFailed,
                    $"iRacing did not come to the front within {_options.FocusTimeout.TotalMilliseconds:F0} ms");
                return;
            }
        }

        attempt.FocusTime = _clock.Elapsed - t;

        t = _clock.Elapsed;
        ct.ThrowIfCancellationRequested();
        attempt.ChatOpen = true;
        _chat.BeginChat();
        await _clock.Delay(_options.ChatOpenDelay, ct).ConfigureAwait(false);
        if (_focus.GetForeground() != sim)
        {
            attempt.Fail(TypedChatFailure.FocusLost, "focus left iRacing after begin-chat");
            return;
        }

        attempt.ChatOpenTime = _clock.Elapsed - t;

        t = _clock.Elapsed;
        if (_options.InterKeyDelay <= TimeSpan.Zero)
        {
            if (!_keyboard.Send(strokes))
            {
                attempt.Fail(TypedChatFailure.KeyboardRejected, "Windows rejected the keystrokes (is iRacing running elevated?)");
                return;
            }
        }
        else
        {
            for (var i = 0; i < strokes.Count; i++)
            {
                if (_focus.GetForeground() != sim)
                {
                    attempt.Fail(TypedChatFailure.FocusLost, $"focus left iRacing after {i} of {strokes.Count} characters");
                    return;
                }

                if (!_keyboard.Send([strokes[i]]))
                {
                    attempt.Fail(TypedChatFailure.KeyboardRejected, $"Windows rejected character {i + 1} of {strokes.Count}");
                    return;
                }

                await _clock.Delay(_options.InterKeyDelay, ct).ConfigureAwait(false);
            }
        }

        if (_options.BeforeEnterDelay > TimeSpan.Zero)
        {
            await _clock.Delay(_options.BeforeEnterDelay, ct).ConfigureAwait(false);
        }

        if (_focus.GetForeground() != sim)
        {
            attempt.Fail(TypedChatFailure.FocusLost, "focus left iRacing while typing; text may have gone to another window");
            return;
        }

        if (!_keyboard.SendEnter())
        {
            attempt.Fail(TypedChatFailure.KeyboardRejected, "Windows rejected the Enter key");
            return;
        }

        // Enter submits and closes the chat box.
        attempt.ChatOpen = false;
        await _clock.Delay(_options.AfterEnterDelay, CancellationToken.None).ConfigureAwait(false);
        attempt.TypingTime = _clock.Elapsed - t;

        if (_chatBox is not null)
        {
            await CheckChatBoxAsync(attempt, text, sim, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The sim sometimes ignores the Enter and leaves the line in the chat box (acceptance run; not reproduced in
    /// spike 15). Read the box: empty means sent. Exactly our line means it was never sent, so a second Enter is
    /// not a repeat; then read again. Anything else is cleared and reported. The box is open after each read,
    /// so the caller's cancel always runs.
    /// </summary>
    private async Task CheckChatBoxAsync(Attempt attempt, string text, WindowHandle sim, CancellationToken ct)
    {
        var t = _clock.Elapsed;
        try
        {
            attempt.ChatOpen = true;
            if (_focus.GetForeground() != sim)
            {
                attempt.ChatBox = ChatBoxCheck.Unreadable;
                attempt.Note = "chat box not checked: iRacing was no longer in front; chat cancelled as a precaution";
                return;
            }

            var first = await _chatBox!.PeekAsync(ct).ConfigureAwait(false);
            if (!first.Read)
            {
                attempt.ChatBox = ChatBoxCheck.Unreadable;
                attempt.Note = $"chat box not checked ({first.Detail}); chat cancelled as a precaution";
                return;
            }

            if (first.Text is null)
            {
                attempt.ChatBox = ChatBoxCheck.Empty;
                return;
            }

            if (!Same(first.Text, text))
            {
                attempt.ChatBox = ChatBoxCheck.UnexpectedText;
                if (first.Text.Contains(text.Trim(), StringComparison.Ordinal))
                {
                    // Our line, mixed with other text: not sent. A second Enter would send the other text too.
                    attempt.Fail(TypedChatFailure.UnexpectedChatText, $"the line was not sent: the chat box held '{first.Text}'; it was cleared");
                }
                else
                {
                    // Our line is gone, so it was sent; something else is in the box.
                    attempt.Note = $"the chat box held other text afterwards ('{first.Text}'); it was cleared";
                }

                return;
            }

            if (!_keyboard.SendEnter())
            {
                attempt.Fail(TypedChatFailure.KeyboardRejected, "Windows rejected the second Enter");
                return;
            }

            await _clock.Delay(_options.AfterEnterDelay, CancellationToken.None).ConfigureAwait(false);
            var second = await _chatBox.PeekAsync(ct).ConfigureAwait(false);
            if (second.Read && second.Text is null)
            {
                attempt.ChatBox = ChatBoxCheck.SentOnRetry;
                attempt.Note = "the sim ignored the first Enter; a second Enter sent the line";
                return;
            }

            attempt.ChatBox = ChatBoxCheck.NotSent;
            attempt.Fail(TypedChatFailure.LineNotSent, second.Read
                ? $"the line stayed in the chat box after a second Enter and was cleared: '{second.Text}'"
                : $"the line was still in the chat box after the first Enter, and the box could not be read after the second ({second.Detail}); chat cancelled");
        }
        finally
        {
            attempt.ChatCheckTime = _clock.Elapsed - t;
        }
    }

    private static bool Same(string boxText, string line) =>
        string.Equals(boxText.Trim(), line.Trim(), StringComparison.Ordinal);

    private async Task<bool> WaitForForegroundAsync(WindowHandle window, CancellationToken ct)
    {
        var deadline = _clock.Elapsed + _options.FocusTimeout;
        while (true)
        {
            if (_focus.GetForeground() == window)
            {
                return true;
            }

            if (_clock.Elapsed >= deadline)
            {
                return false;
            }

            await _clock.Delay(_options.FocusPollInterval, ct).ConfigureAwait(false);
        }
    }

    private TypedChatResult Early(TypedChatFailure failure, string detail, TimeSpan start) => new()
    {
        Success = false,
        Failure = failure,
        Detail = detail,
        SimWasForeground = false,
        PreviousProcess = "(not checked)",
        ChatCancelled = false,
        FocusRestored = true,
        FocusTime = TimeSpan.Zero,
        ChatOpenTime = TimeSpan.Zero,
        TypingTime = TimeSpan.Zero,
        RestoreTime = TimeSpan.Zero,
        TotalTime = _clock.Elapsed - start,
    };

    private sealed class Attempt
    {
        public TypedChatFailure Failure { get; private set; }
        public string? Detail { get; set; }
        public string? FocusMethod { get; set; }
        public bool ChatOpen { get; set; }
        public TimeSpan FocusTime { get; set; }
        public TimeSpan ChatOpenTime { get; set; }
        public TimeSpan TypingTime { get; set; }
        public ChatBoxCheck ChatBox { get; set; } = ChatBoxCheck.Off;
        public string? Note { get; set; }
        public TimeSpan ChatCheckTime { get; set; }

        public void Fail(TypedChatFailure failure, string detail)
        {
            Failure = failure;
            Detail = detail;
        }
    }
}
