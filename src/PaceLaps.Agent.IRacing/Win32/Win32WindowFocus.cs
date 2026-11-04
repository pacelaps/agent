using System.Diagnostics;
using PaceLaps.Agent.Core.TypedChat;
using static PaceLaps.Agent.IRacing.Win32.NativeMethods;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>What the sim window looks like, for spike logs. Window mode cannot be read exactly; this is a hint.</summary>
public sealed record SimWindowInfo(
    string Process,
    bool HasCaption,
    bool IsPopup,
    bool CoversMonitor,
    string Rect,
    string Monitor)
{
    /// <summary>"windowed", or "borderless-or-fullscreen" (the two cannot be told apart from window styles).</summary>
    public string ModeHint => HasCaption ? "windowed" : CoversMonitor ? "borderless-or-fullscreen" : "borderless-not-maximised";
}

public sealed class Win32WindowFocus : IWindowFocus
{
    /// <summary>Process names of the iRacing simulator, newest first.</summary>
    private static readonly string[] SimProcessNames = ["iRacingSim64DX11", "iRacingSim64"];

    private nint _cachedSim;

    public WindowHandle GetForeground() => new(GetForegroundWindow());

    public WindowHandle FindSimWindow()
    {
        if (_cachedSim != 0 && NativeMethods.IsWindow(_cachedSim))
        {
            return new WindowHandle(_cachedSim);
        }

        _cachedSim = 0;
        foreach (var name in SimProcessNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.MainWindowHandle != 0)
                    {
                        _cachedSim = p.MainWindowHandle;
                        return new WindowHandle(_cachedSim);
                    }
                }
            }
        }

        return WindowHandle.None;
    }

    public string BringToFront(WindowHandle window)
    {
        var h = window.Value;
        if (IsIconic(h))
        {
            ShowWindow(h, SW_RESTORE);
        }

        if (SetForegroundWindow(h) && GetForegroundWindow() == h)
        {
            return "SetForegroundWindow";
        }

        // Windows only lets the process that received the last input event take the foreground.
        // A press of an unassigned virtual key makes this process that one, without side effects
        // in the current foreground window (an Alt press would open a browser's menu bar).
        SendUnassignedKey();
        if (SetForegroundWindow(h) && GetForegroundWindow() == h)
        {
            return "InputThenSetForegroundWindow";
        }

        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var thisThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != thisThread
            && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(h);
            SetForegroundWindow(h);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(thisThread, foregroundThread, false);
            }
        }

        return "AttachThreadInput";
    }

    public bool IsWindow(WindowHandle window) => NativeMethods.IsWindow(window.Value);

    public string DescribeProcess(WindowHandle window)
    {
        if (window.IsNone)
        {
            return "(none)";
        }

        GetWindowThreadProcessId(window.Value, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (ArgumentException)
        {
            return "(exited)";
        }
        catch (InvalidOperationException)
        {
            return "(exited)";
        }
    }

    public SimWindowInfo? DescribeSimWindow()
    {
        var sim = FindSimWindow();
        if (sim.IsNone)
        {
            return null;
        }

        var style = (uint)GetWindowLong(sim.Value, GWL_STYLE);
        GetWindowRect(sim.Value, out var r);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(sim.Value, MONITOR_DEFAULTTONEAREST), ref info);
        var m = info.rcMonitor;
        var covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;

        return new SimWindowInfo(
            DescribeProcess(sim),
            (style & WS_CAPTION) == WS_CAPTION,
            (style & WS_POPUP) != 0,
            covers,
            $"{r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top}",
            $"{m.Left},{m.Top},{m.Right - m.Left}x{m.Bottom - m.Top}");
    }

    /// <summary>The keyboard layout of the sim's input thread, or this thread's layout if the sim is not found.</summary>
    public nint SimKeyboardLayout()
    {
        var sim = FindSimWindow();
        var thread = sim.IsNone ? 0 : GetWindowThreadProcessId(sim.Value, out _);
        return GetKeyboardLayout(thread);
    }

    private static void SendUnassignedKey()
    {
        const ushort unassignedVk = 0xE8;
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = unassignedVk } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = unassignedVk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
}
