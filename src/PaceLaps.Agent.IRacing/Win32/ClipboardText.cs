using System.Runtime.InteropServices;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>
/// Plain-text clipboard access through Win32 (no STA thread needed). Used by the chat readback, which saves and
/// restores the operator's clipboard text. Other clipboard formats (images, files) are not preserved.
/// </summary>
public static class ClipboardText
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);

    /// <summary>The clipboard text, or null when it holds no text (or cannot be opened).</summary>
    public static string? Get()
    {
        if (!Open())
        {
            return null;
        }

        try
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText))
            {
                return null;
            }

            var handle = GetClipboardData(CfUnicodeText);
            if (handle == 0)
            {
                return null;
            }

            var p = GlobalLock(handle);
            try
            {
                return p == 0 ? null : Marshal.PtrToStringUni(p);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Replaces the clipboard with this text; null empties it. Returns false if the clipboard was busy.</summary>
    public static bool Set(string? text)
    {
        if (!Open())
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            if (text is null)
            {
                return true;
            }

            var bytes = (nuint)((text.Length + 1) * 2);
            var handle = GlobalAlloc(GmemMoveable, bytes);
            if (handle == 0)
            {
                return false;
            }

            var p = GlobalLock(handle);
            try
            {
                var chars = (text + "\0").ToCharArray();
                Marshal.Copy(chars, 0, p, chars.Length);
            }
            finally
            {
                GlobalUnlock(handle);
            }

            if (SetClipboardData(CfUnicodeText, handle) == 0)
            {
                GlobalFree(handle);
                return false;
            }

            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool Open()
    {
        // Another program may hold the clipboard for a moment.
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(0))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }
}
