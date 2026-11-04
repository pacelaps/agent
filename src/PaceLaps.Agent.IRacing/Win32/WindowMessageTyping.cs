using System.Runtime.InteropServices;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>How Enter is posted after the text.</summary>
public enum PostedEnter
{
    /// <summary>WM_KEYDOWN and WM_KEYUP with VK_RETURN.</summary>
    Key,

    /// <summary>WM_CHAR with a carriage return.</summary>
    Char,
}

/// <summary>How the messages reach the window.</summary>
public enum MessageDelivery
{
    /// <summary>
    /// PostMessage: queued, returns at once. IRSDKSharper posts the SDK's broadcasts too, so begin chat, the text,
    /// Enter and cancel chat reach the sim in the order they were posted.
    /// </summary>
    Post,

    /// <summary>
    /// SendMessageTimeout: handled before the call returns; gives up on a hung window. Windows handles sent messages
    /// before queued ones, so text sent this way can overtake a begin chat that is still queued.
    /// </summary>
    Send,
}

/// <summary>What posting did: how many messages Windows accepted, and the first error.</summary>
public sealed record PostResult(int Posted, int Failed, int? FirstError);

/// <summary>
/// Spike 16, and the typed channel's posted way through <see cref="PostedChatInput"/> (spec-feedback 37): puts text
/// into a window without bringing it to the front, by posting or sending window messages to it
/// (WM_CHAR per character, WM_PASTE, Enter as key or character). Windows refuses posts to a process running at a higher
/// integrity level (an iRacing started as administrator), which shows as failed posts. Nothing is injected into
/// the sim's process.
/// </summary>
public static class WindowMessageTyping
{
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const uint WM_PASTE = 0x0302;
    private const int VK_RETURN = 0x0D;
    private const int ScanEnter = 0x1C;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SendTimeoutMs = 500;

    public static PostResult PostText(nint window, string text, MessageDelivery delivery = MessageDelivery.Post)
    {
        var results = new List<bool>(text.Length);
        int? firstError = null;
        foreach (var c in text)
        {
            Post(window, WM_CHAR, c, 1, results, ref firstError, delivery);
        }

        return Summarize(results, firstError);
    }

    public static PostResult PostEnter(nint window, PostedEnter how, MessageDelivery delivery = MessageDelivery.Post)
    {
        var results = new List<bool>(2);
        int? firstError = null;
        if (how == PostedEnter.Char)
        {
            Post(window, WM_CHAR, '\r', 1 | (ScanEnter << 16), results, ref firstError, delivery);
        }
        else
        {
            Post(window, WM_KEYDOWN, VK_RETURN, 1 | (ScanEnter << 16), results, ref firstError, delivery);
            // Key up: previous state down (bit 30) and transition (bit 31).
            Post(window, WM_KEYUP, VK_RETURN, unchecked(1 | (ScanEnter << 16) | (1 << 30) | (int)0x80000000), results, ref firstError, delivery);
        }

        return Summarize(results, firstError);
    }

    /// <summary>Asks the window to paste the clipboard (edit controls handle it; a game may not).</summary>
    public static PostResult PostPaste(nint window)
    {
        var results = new List<bool>(1);
        int? firstError = null;
        Post(window, WM_PASTE, 0, 0, results, ref firstError, MessageDelivery.Post);
        return Summarize(results, firstError);
    }

    /// <summary>The window's class and its child windows' classes, for the log (a chat box may be a child window).</summary>
    public static (string Class, IReadOnlyList<string> Children) Describe(nint window)
    {
        var children = new List<string>();
        EnumChildWindows(window, (child, _) =>
        {
            children.Add($"{ClassOf(child)} 0x{(long)child:X}");
            return true;
        }, 0);
        return (ClassOf(window), children);
    }

    private static void Post(nint window, uint msg, nint wParam, nint lParam, List<bool> results, ref int? firstError, MessageDelivery delivery)
    {
        var ok = delivery == MessageDelivery.Post
            ? PostMessage(window, msg, wParam, lParam)
            : SendMessageTimeout(window, msg, wParam, lParam, SMTO_ABORTIFHUNG, SendTimeoutMs, out _) != 0;
        if (!ok)
        {
            firstError ??= Marshal.GetLastWin32Error();
        }

        results.Add(ok);
    }

    private static PostResult Summarize(List<bool> results, int? firstError) =>
        new(results.Count(r => r), results.Count(r => !r), firstError);

    private static string ClassOf(nint window)
    {
        var buffer = new char[256];
        var length = GetClassName(window, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, length));
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, nint lParam, uint fuFlags, uint uTimeout, out nint lpdwResult);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, char[] lpClassName, int nMaxCount);
}
