using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>One thing the monitor saw, stamped when it happened.</summary>
public sealed record MonitorEvent(string Kind, DateTimeOffset At, object Data);

/// <summary>
/// Watches, without touching any other process, how a program might get text into iRacing (spike 16): the front
/// window, keystrokes and mouse input that programs inject (SendInput), clipboard changes, and the SDK's broadcast
/// messages (every top-level window receives them). Low-level hooks and out-of-context WinEvents run in this
/// process; nothing is injected into iRacing, which has anti-cheat.
///
/// Privacy: real (not injected) keystrokes are counted, never recorded with their key. Window titles and clipboard
/// contents are never read: the front window is named by its process and class, the clipboard by its owner.
/// </summary>
public sealed class InputMonitor : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLKHF_UP = 0x80;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x02;
    private const int WM_MOUSEMOVE = 0x0200;
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_QUIT = 0x0012;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Channel<MonitorEvent> _events = Channel.CreateUnbounded<MonitorEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<uint, string> _processNames = new();
    private readonly TaskCompletionSource<string?> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    // Kept as fields: the native side calls them for as long as the hooks live.
    private readonly LowLevelProc _keyboardProc;
    private readonly LowLevelProc _mouseProc;
    private readonly WinEventProc _foregroundProc;
    private readonly WndProc _wndProc;

    private uint _threadId;
    private uint _broadcastMessage;
    private long _lastInjectedMoveTicks;

    public InputMonitor()
    {
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        _foregroundProc = OnForeground;
        _wndProc = OnWindowMessage;
        _thread = new Thread(Run) { IsBackground = true, Name = "spike16-monitor" };
    }

    public ChannelReader<MonitorEvent> Events => _events.Reader;

    /// <summary>Starts the hooks on their own message thread. Returns null, or what could not be set up.</summary>
    public Task<string?> StartAsync()
    {
        _thread.Start();
        return _started.Task;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _events.Writer.TryComplete();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var module = GetModuleHandle(null);
        var problems = new List<string>();

        var keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        if (keyboardHook == 0)
        {
            problems.Add($"keyboard hook failed ({Marshal.GetLastWin32Error()})");
        }

        var mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        if (mouseHook == 0)
        {
            problems.Add($"mouse hook failed ({Marshal.GetLastWin32Error()})");
        }

        var foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0, _foregroundProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (foregroundHook == 0)
        {
            problems.Add("foreground hook failed");
        }

        // A hidden top-level window: broadcasts reach only top-level windows (not message-only ones).
        _broadcastMessage = RegisterWindowMessage(SdkBroadcast.MessageName);
        var className = "PaceLapsSpike16Monitor";
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = module,
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        var window = CreateWindowEx(WS_EX_TOOLWINDOW, className, "", WS_POPUP, 0, 0, 0, 0, 0, 0, module, 0);
        if (window == 0)
        {
            problems.Add($"listener window failed ({Marshal.GetLastWin32Error()})");
        }
        else if (!AddClipboardFormatListener(window))
        {
            problems.Add($"clipboard listener failed ({Marshal.GetLastWin32Error()})");
        }

        _started.TrySetResult(problems.Count == 0 ? null : string.Join("; ", problems));

        while (GetMessage(out var msg, 0, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (window != 0)
        {
            RemoveClipboardFormatListener(window);
            DestroyWindow(window);
        }

        UnhookWinEvent(foregroundHook);
        UnhookWindowsHookEx(mouseHook);
        UnhookWindowsHookEx(keyboardHook);
    }

    private nint OnKeyboard(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var injected = (k.flags & LLKHF_INJECTED) != 0;
            var up = (k.flags & LLKHF_UP) != 0;
            Emit("key", injected
                ? new
                {
                    injected = true,
                    lowerIntegrity = (k.flags & LLKHF_LOWER_IL_INJECTED) != 0,
                    up,
                    vk = $"0x{k.vkCode:X2}",
                    scan = $"0x{k.scanCode:X2}",
                    extended = (k.flags & LLKHF_EXTENDED) != 0,
                    extraInfo = (long)k.dwExtraInfo,
                    foreground = Describe(GetForegroundWindow()),
                }
                : new { injected = false, up });
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private nint OnMouse(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // Real mouse input is not recorded at all; injected moves at most every 100 ms.
            if ((m.flags & LLMHF_INJECTED) != 0)
            {
                var now = Stopwatch.GetTimestamp();
                var move = (int)wParam == WM_MOUSEMOVE;
                if (!move || Stopwatch.GetElapsedTime(_lastInjectedMoveTicks, now) > TimeSpan.FromMilliseconds(100))
                {
                    if (move)
                    {
                        _lastInjectedMoveTicks = now;
                    }

                    Emit("mouse", new
                    {
                        injected = true,
                        lowerIntegrity = (m.flags & LLMHF_LOWER_IL_INJECTED) != 0,
                        message = $"0x{(int)wParam:X4}",
                        x = m.pt.X,
                        y = m.pt.Y,
                        foreground = Describe(GetForegroundWindow()),
                    });
                }
            }
        }

        return CallNextHookEx(0, nCode, wParam, lParam);
    }

    private void OnForeground(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time) =>
        Emit("foreground", new { window = Describe(hwnd) });

    private nint OnWindowMessage(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            Emit("clipboard", new { owner = Describe(GetClipboardOwner()), sequence = GetClipboardSequenceNumber() });
            return 0;
        }

        if (_broadcastMessage != 0 && msg == _broadcastMessage)
        {
            var (message, var1, var2, description) = SdkBroadcast.Decode(wParam, lParam);
            Emit("sdk_broadcast", new { message, var1, var2, description });
            return 0;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void Emit(string kind, object data) => _events.Writer.TryWrite(new MonitorEvent(kind, DateTimeOffset.UtcNow, data));

    /// <summary>A window by process and class: never the title, which can hold personal data.</summary>
    private object? Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var pid);
        var process = _processNames.GetOrAdd(pid, static id =>
        {
            try
            {
                using var p = Process.GetProcessById((int)id);
                return p.ProcessName;
            }
            catch (ArgumentException)
            {
                return $"pid {id}";
            }
        });
        var buffer = new char[256];
        var length = GetClassName(hwnd, buffer, buffer.Length);
        return new { process, windowClass = new string(buffer, 0, Math.Max(0, length)), handle = $"0x{(long)hwnd:X}" };
    }

    private delegate nint LowLevelProc(int nCode, nint wParam, nint lParam);

    private delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle, int x, int y,
        int nWidth, int nHeight, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, char[] lpClassName, int nMaxCount);
}
