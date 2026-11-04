using System.Runtime.InteropServices;
using PaceLaps.Agent.Core.TypedChat;
using static PaceLaps.Agent.IRacing.Win32.NativeMethods;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>
/// Types with hardware scancodes through SendInput. iRacing translates scancodes with its own active layout,
/// so text is mapped with the layout of the sim's input thread.
/// </summary>
public sealed class ScancodeKeyboard : IKeyboard
{
    private const ushort ScanLeftShift = 0x2A;
    private const ushort ScanLeftCtrl = 0x1D;
    private const ushort ScanAlt = 0x38; // Right Alt (AltGr) when sent as extended.
    private const ushort ScanEnter = 0x1C;

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    private readonly Func<nint> _layout;

    public ScancodeKeyboard(Func<nint> layout)
    {
        _layout = layout;
    }

    public bool TryMap(string text, out IReadOnlyList<KeyStroke> strokes, out char unmappable) =>
        ScancodeMapper.TryMap(text, new Win32KeyboardLayout(_layout()), out strokes, out unmappable);

    public bool Send(IReadOnlyList<KeyStroke> strokes)
    {
        var inputs = new List<INPUT>(strokes.Count * 6);
        foreach (var s in strokes)
        {
            AddStroke(inputs, s);
        }

        return SendAll(inputs);
    }

    /// <summary>Ctrl plus one key by scancode, for chat editing shortcuts (Ctrl+A select all, Ctrl+C copy).</summary>
    public bool SendCtrlChord(ushort scanCode)
    {
        var inputs = new List<INPUT>(4);
        AddKey(inputs, ScanLeftCtrl, false, false);
        AddKey(inputs, scanCode, false, false);
        AddKey(inputs, scanCode, false, true);
        AddKey(inputs, ScanLeftCtrl, false, true);
        return SendAll(inputs);
    }

    /// <summary>Scancode of the A key (the same position on QWERTY and QWERTZ).</summary>
    public const ushort ScanA = 0x1E;

    /// <summary>Scancode of the C key (the same position on QWERTY and QWERTZ).</summary>
    public const ushort ScanC = 0x2E;

    /// <summary>Scancode of the V key (the same position on QWERTY and QWERTZ).</summary>
    public const ushort ScanV = 0x2F;

    public bool SendEnter()
    {
        var inputs = new List<INPUT>(2);
        AddKey(inputs, ScanEnter, extended: false, up: false);
        AddKey(inputs, ScanEnter, extended: false, up: true);
        return SendAll(inputs);
    }

    private static void AddStroke(List<INPUT> inputs, KeyStroke s)
    {
        var shift = s.Modifiers.HasFlag(KeyModifiers.Shift);
        var altGr = s.Modifiers.HasFlag(KeyModifiers.Ctrl) && s.Modifiers.HasFlag(KeyModifiers.Alt);

        if (altGr)
        {
            AddKey(inputs, ScanLeftCtrl, false, false);
            AddKey(inputs, ScanAlt, true, false);
        }

        if (shift)
        {
            AddKey(inputs, ScanLeftShift, false, false);
        }

        AddKey(inputs, s.ScanCode, s.Extended, false);
        AddKey(inputs, s.ScanCode, s.Extended, true);

        if (shift)
        {
            AddKey(inputs, ScanLeftShift, false, true);
        }

        if (altGr)
        {
            AddKey(inputs, ScanAlt, true, true);
            AddKey(inputs, ScanLeftCtrl, false, true);
        }
    }

    private static void AddKey(List<INPUT> inputs, ushort scan, bool extended, bool up)
    {
        var flags = KEYEVENTF_SCANCODE;
        if (extended)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        if (up)
        {
            flags |= KEYEVENTF_KEYUP;
        }

        inputs.Add(new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wScan = scan, dwFlags = flags } } });
    }

    private static bool SendAll(List<INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        var array = inputs.ToArray();
        return SendInput((uint)array.Length, array, InputSize) == array.Length;
    }
}
