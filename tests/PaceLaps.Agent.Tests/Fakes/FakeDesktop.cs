using PaceLaps.Agent.Core.TypedChat;

namespace PaceLaps.Agent.Tests.Fakes;

/// <summary>
/// One fake for window focus, keyboard and the SDK chat messages, so a test can check the order of calls
/// and inject failures at each step.
/// </summary>
internal sealed class FakeDesktop : IWindowFocus, IKeyboard, ISimChat, IChatBoxReader, IChatPoster
{
    /// <summary>Windows refuses the window messages (an elevated iRacing).</summary>
    public bool RejectPosts { get; set; }

    /// <summary>How many of the next posted lines the sim loses: the Enter does nothing, the line stays in the box.</summary>
    public int LosePostedLines { get; set; }

    /// <summary>Posted text goes into the open chat box whichever window is in front (spike 16).</summary>
    public bool PostText(WindowHandle window, string text)
    {
        Calls.Add($"post:{text.Length}");
        if (RejectPosts || window != Sim)
        {
            return false;
        }

        if (ChatOpen)
        {
            Line += text;
        }

        return true;
    }

    public bool PostEnter(WindowHandle window)
    {
        Calls.Add("post-enter");
        if (RejectPosts || window != Sim)
        {
            return false;
        }

        if (LosePostedLines > 0)
        {
            LosePostedLines--;
            return true;
        }

        if (ChatOpen && Line.Length > 0)
        {
            SentLines.Add(Line);
        }

        Line = "";
        ChatOpen = false;
        return true;
    }

    public static readonly WindowHandle Sim = new(0x100);
    public static readonly WindowHandle Browser = new(0x200);

    private readonly FakeClock _clock;

    public FakeDesktop(FakeClock clock)
    {
        _clock = clock;
    }

    public List<string> Calls { get; } = [];
    public WindowHandle Foreground { get; set; } = Browser;
    public bool SimRunning { get; set; } = true;
    public bool SimRefusesFocus { get; set; }
    public bool BrowserRefusesFocus { get; set; }
    public bool BrowserClosed { get; set; }
    public TimeSpan FocusCost { get; set; } = TimeSpan.FromMilliseconds(20);
    public bool RejectKeys { get; set; }
    public bool RejectEnter { get; set; }
    public Exception? ThrowOnSend { get; set; }

    /// <summary>Simulates the operator clicking another window after this many characters.</summary>
    public int? StealFocusAfterChars { get; set; }

    public string Typed { get; private set; } = "";

    public bool ChatOpen { get; private set; }

    public WindowHandle GetForeground() => Foreground;

    public WindowHandle FindSimWindow() => SimRunning ? Sim : WindowHandle.None;

    public string BringToFront(WindowHandle window)
    {
        Calls.Add($"front:{Name(window)}");
        _clock.Advance(FocusCost);
        var refuses = window == Sim ? SimRefusesFocus : BrowserRefusesFocus;
        if (!refuses)
        {
            Foreground = window;
        }

        return "fake";
    }

    public bool IsWindow(WindowHandle window) => !(window == Browser && BrowserClosed);

    public string DescribeProcess(WindowHandle window) => Name(window) + ".exe";

    public bool TryMap(string text, out IReadOnlyList<KeyStroke> strokes, out char unmappable)
    {
        var list = new List<KeyStroke>();
        foreach (var c in text)
        {
            if (c < ' ' || c == '^')
            {
                strokes = [];
                unmappable = c;
                return false;
            }

            list.Add(new KeyStroke(0x10, false, KeyModifiers.None, c));
        }

        strokes = list;
        unmappable = '\0';
        return true;
    }

    public bool Send(IReadOnlyList<KeyStroke> strokes)
    {
        Calls.Add($"keys:{strokes.Count}");
        if (ThrowOnSend is not null)
        {
            throw ThrowOnSend;
        }

        if (RejectKeys)
        {
            return false;
        }

        foreach (var s in strokes)
        {
            if (Foreground == Sim && ChatOpen)
            {
                Typed += s.Character;
                Line += s.Character;
            }

            if (StealFocusAfterChars is { } n && Typed.Length >= n)
            {
                Foreground = Browser;
            }
        }

        _clock.Advance(TimeSpan.FromMilliseconds(strokes.Count));
        return true;
    }

    public bool SendEnter()
    {
        Calls.Add("enter");
        if (RejectEnter)
        {
            return false;
        }

        if (IgnoreEnters > 0)
        {
            // The sim ignores this Enter: the line stays in the open chat box.
            IgnoreEnters--;
            return true;
        }

        if (ChatOpen && Line.Length > 0)
        {
            SentLines.Add(Line);
        }

        Line = BoxTextAfterEnter ?? "";
        ChatOpen = BoxTextAfterEnter is not null;
        return true;
    }

    public void BeginChat()
    {
        Calls.Add("begin");
        ChatOpen = true;
    }

    public void CancelChat()
    {
        Calls.Add("cancel");
        ChatOpen = false;
        Line = "";
    }

    /// <summary>Lines the sim actually sent (Enter on a non-empty open chat box).</summary>
    public List<string> SentLines { get; } = [];

    /// <summary>What is in the chat box now; begin-chat keeps it, cancel clears it (spike 15).</summary>
    public string Line { get; set; } = "";

    /// <summary>How many of the next Enters the sim ignores.</summary>
    public int IgnoreEnters { get; set; }

    /// <summary>Text the box holds after an Enter (simulates something else in the box).</summary>
    public string? BoxTextAfterEnter { get; set; }

    public bool PeekUnreadable { get; set; }

    public Task<ChatBoxContent> PeekAsync(CancellationToken ct)
    {
        Calls.Add("peek");
        _clock.Advance(TimeSpan.FromMilliseconds(300));
        if (PeekUnreadable)
        {
            return Task.FromResult(ChatBoxContent.Unreadable("clipboard busy"));
        }

        ChatOpen = true;
        return Task.FromResult(Line.Length == 0 ? ChatBoxContent.Empty : new ChatBoxContent(true, Line));
    }

    private static string Name(WindowHandle w) => w == Sim ? "sim" : w == Browser ? "browser" : w.ToString();
}
