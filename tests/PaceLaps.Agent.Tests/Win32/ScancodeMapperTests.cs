using PaceLaps.Agent.Core.TypedChat;
using PaceLaps.Agent.IRacing.Win32;

namespace PaceLaps.Agent.Tests.Win32;

public sealed class ScancodeMapperTests
{
    /// <summary>A few keys of the German layout, as VkKeyScanEx and MapVirtualKeyEx report them.</summary>
    private sealed class FakeGermanLayout : IKeyboardLayout
    {
        private static readonly Dictionary<char, short> Keys = new()
        {
            ['a'] = 0x0041,
            ['A'] = 0x0141,
            ['1'] = 0x0031,
            ['!'] = 0x0131,        // Shift+1
            ['#'] = 0x00BF,        // OEM_2, own key
            ['/'] = 0x0137,        // Shift+7
            ['@'] = 0x0651,        // AltGr+Q
            [' '] = 0x0020,
            ['^'] = 0x00DC,        // OEM_5, dead key
            ['\u0001'] = 0x0241,   // Ctrl+A
        };

        private static readonly Dictionary<byte, uint> Scan = new()
        {
            [0x41] = 0x1E,
            [0x31] = 0x02,
            [0x37] = 0x08,
            [0xBF] = 0x2B,
            [0x51] = 0x10,
            [0x20] = 0x39,
            [0xDC] = 0x29,
            [0x6F] = 0xE035,       // numpad divide, extended
        };

        public short VkKeyScan(char c) => Keys.TryGetValue(c, out var v) ? v : (short)-1;

        public uint ScanCode(byte vk) => Scan.TryGetValue(vk, out var v) ? v : 0;

        public bool IsDeadKey(byte vk) => vk == 0xDC;
    }

    private static readonly FakeGermanLayout Layout = new();

    [Fact]
    public void Maps_plain_shifted_and_AltGr_characters()
    {
        Assert.True(ScancodeMapper.TryMap("a!#/@ A", Layout, out var strokes, out _));

        Assert.Equal(
        [
            new KeyStroke(0x1E, false, KeyModifiers.None, 'a'),
            new KeyStroke(0x02, false, KeyModifiers.Shift, '!'),
            new KeyStroke(0x2B, false, KeyModifiers.None, '#'),
            new KeyStroke(0x08, false, KeyModifiers.Shift, '/'),
            new KeyStroke(0x10, false, KeyModifiers.Ctrl | KeyModifiers.Alt, '@'),
            new KeyStroke(0x39, false, KeyModifiers.None, ' '),
            new KeyStroke(0x1E, false, KeyModifiers.Shift, 'A'),
        ], strokes);
    }

    [Theory]
    [InlineData("a\n", '\n')]
    [InlineData("a\r", '\r')]
    [InlineData("a\t", '\t')]
    [InlineData("^", '^')]
    [InlineData("€", '€')]
    [InlineData("\u0001", '\u0001')]
    public void Rejects_control_characters_dead_keys_and_characters_not_on_the_layout(string text, char expected)
    {
        Assert.False(ScancodeMapper.TryMap(text, Layout, out var strokes, out var bad));
        Assert.Equal(expected, bad);
        Assert.Empty(strokes);
    }

    [Fact]
    public void Rejects_surrogate_pairs()
    {
        Assert.False(ScancodeMapper.TryMap("a 🏁", Layout, out _, out var bad));
        Assert.True(char.IsSurrogate(bad));
    }

    [Fact]
    public void Empty_text_maps_to_no_strokes()
    {
        Assert.True(ScancodeMapper.TryMap("", Layout, out var strokes, out _));
        Assert.Empty(strokes);
    }
}
