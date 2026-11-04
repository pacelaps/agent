namespace PaceLaps.Agent.IRacing.Sdk;

/// <summary>Readable names for the SDK's flag and enum values, for logs and the console.</summary>
public static class SdkNames
{
    public const uint FlagCheckered = 0x00000001;
    public const uint FlagGreen = 0x00000004;
    public const uint FlagRed = 0x00000010;
    public const uint FlagYellow = 0x00000008;
    public const uint FlagCaution = 0x00004000;
    public const uint FlagCautionWaving = 0x00008000;
    public const uint FlagOneLapToGreen = 0x00000200;
    public const uint FlagBlack = 0x00010000;

    public const uint PaceEndOfLine = 0x01;
    public const uint PaceFreePass = 0x02;
    public const uint PaceWavedAround = 0x04;

    public const int SurfaceOnTrack = 3;

    public const int PaceModeSingleFileRestart = 2;
    public const int PaceModeDoubleFileRestart = 3;

    private static readonly (uint Bit, string Name)[] SessionFlagNames =
    [
        (0x00000001, "checkered"), (0x00000002, "white"), (0x00000004, "green"), (0x00000008, "yellow"),
        (0x00000010, "red"), (0x00000020, "blue"), (0x00000040, "debris"), (0x00000080, "crossed"),
        (0x00000100, "yellowWaving"), (0x00000200, "oneLapToGreen"), (0x00000400, "greenHeld"),
        (0x00000800, "tenToGo"), (0x00001000, "fiveToGo"), (0x00002000, "randomWaving"),
        (0x00004000, "caution"), (0x00008000, "cautionWaving"), (0x00010000, "black"),
        (0x00020000, "disqualify"), (0x00040000, "servicible"), (0x00080000, "furled"),
        (0x00100000, "repair"), (0x10000000, "startHidden"), (0x20000000, "startReady"),
        (0x40000000, "startSet"), (0x80000000, "startGo"),
    ];

    private static readonly (uint Bit, string Name)[] PaceFlagNames =
        [(PaceEndOfLine, "endOfLine"), (PaceFreePass, "freePass"), (PaceWavedAround, "wavedAround")];

    public static string SessionFlags(uint flags) => Bits(flags, SessionFlagNames);

    public static string PaceFlags(uint flags) => Bits(flags, PaceFlagNames);

    public static string PaceMode(int mode) => mode switch
    {
        0 => "singleFileStart",
        1 => "doubleFileStart",
        2 => "singleFileRestart",
        3 => "doubleFileRestart",
        4 => "notPacing",
        _ => mode.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static string TrackSurface(int surface) => surface switch
    {
        -1 => "notInWorld",
        0 => "offTrack",
        1 => "inPitStall",
        2 => "approachingPits",
        3 => "onTrack",
        _ => surface.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    public static string SessionState(int state) => state switch
    {
        0 => "invalid",
        1 => "getInCar",
        2 => "warmup",
        3 => "paradeLaps",
        4 => "racing",
        5 => "checkered",
        6 => "coolDown",
        _ => state.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Bits(uint value, (uint Bit, string Name)[] names)
    {
        var parts = names.Where(n => (value & n.Bit) != 0).Select(n => n.Name).ToList();
        var known = names.Aggregate(0u, (acc, n) => acc | n.Bit);
        if ((value & ~known) != 0)
        {
            parts.Add($"0x{value & ~known:X}");
        }

        return parts.Count == 0 ? "none" : string.Join("|", parts);
    }
}
