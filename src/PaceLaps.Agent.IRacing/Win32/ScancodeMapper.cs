using PaceLaps.Agent.Core.TypedChat;

namespace PaceLaps.Agent.IRacing.Win32;

/// <summary>The two layout lookups the mapper needs. Faked in tests so mapping is tested without a real layout.</summary>
internal interface IKeyboardLayout
{
    /// <summary>VkKeyScanEx: low byte virtual key, high byte shift state (1 Shift, 2 Ctrl, 4 Alt); -1 if not on the layout.</summary>
    short VkKeyScan(char c);

    /// <summary>MapVirtualKeyEx with MAPVK_VK_TO_VSC_EX: scancode, with 0xE0 in the high byte for extended keys.</summary>
    uint ScanCode(byte vk);

    /// <summary>True if the virtual key is a dead key (MapVirtualKeyEx MAPVK_VK_TO_CHAR has the high bit set).</summary>
    bool IsDeadKey(byte vk);
}

internal sealed class Win32KeyboardLayout(nint hkl) : IKeyboardLayout
{
    public short VkKeyScan(char c) => NativeMethods.VkKeyScanEx(c, hkl);

    public uint ScanCode(byte vk) => NativeMethods.MapVirtualKeyEx(vk, NativeMethods.MAPVK_VK_TO_VSC_EX, hkl);

    public bool IsDeadKey(byte vk) => (NativeMethods.MapVirtualKeyEx(vk, NativeMethods.MAPVK_VK_TO_CHAR, hkl) & 0x80000000) != 0;
}

internal static class ScancodeMapper
{
    public static bool TryMap(string text, IKeyboardLayout layout, out IReadOnlyList<KeyStroke> strokes, out char unmappable)
    {
        var list = new List<KeyStroke>(text.Length);
        foreach (var c in text)
        {
            if (!TryMapChar(c, layout, out var stroke))
            {
                strokes = [];
                unmappable = c;
                return false;
            }

            list.Add(stroke);
        }

        strokes = list;
        unmappable = '\0';
        return true;
    }

    private static bool TryMapChar(char c, IKeyboardLayout layout, out KeyStroke stroke)
    {
        stroke = default;

        // Control characters would act as keys (Enter, Escape, Backspace) in the chat box.
        if (char.IsControl(c) || char.IsSurrogate(c))
        {
            return false;
        }

        var scan = layout.VkKeyScan(c);
        if (scan == -1)
        {
            return false;
        }

        var vk = (byte)(scan & 0xFF);
        var shiftState = (scan >> 8) & 0xFF;
        KeyModifiers mods;
        switch (shiftState)
        {
            case 0: mods = KeyModifiers.None; break;
            case 1: mods = KeyModifiers.Shift; break;
            case 6: mods = KeyModifiers.Ctrl | KeyModifiers.Alt; break; // AltGr
            case 7: mods = KeyModifiers.Shift | KeyModifiers.Ctrl | KeyModifiers.Alt; break;
            default: return false; // Ctrl or Alt alone are shortcuts, not characters.
        }

        if (layout.IsDeadKey(vk))
        {
            return false;
        }

        var sc = layout.ScanCode(vk);
        if ((sc & 0xFF) == 0)
        {
            return false;
        }

        stroke = new KeyStroke((ushort)(sc & 0xFF), (sc & 0xFF00) == 0xE000, mods, c);
        return true;
    }
}
