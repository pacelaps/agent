using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Core.Snapshots;

/// <summary>
/// Maps iRacing's SessionFlags to the contract's flag vocabulary (common.schema.json flagName). This is a data
/// mapping, not race logic. green is derived (spike 3: the sim's green bit is set only briefly after a green flag).
/// </summary>
public static class FlagNormalizer
{
    private static readonly (uint Bit, string Name)[] Direct =
    [
        (SimValues.FlagCautionWaving, "caution_waving"),
        (SimValues.FlagOneLapToGreen, "one_lap_to_green"),
        (SimValues.FlagGreenHeld, "green_held"),
        (SimValues.FlagWhite, "white"),
        (SimValues.FlagCheckered, "checkered"),
        (SimValues.FlagRed, "red"),
        (SimValues.FlagYellow, "yellow"),
        (SimValues.FlagYellowWaving, "yellow_waving"),
        (SimValues.FlagDebris, "debris"),
        (SimValues.FlagTenToGo, "ten_to_go"),
        (SimValues.FlagFiveToGo, "five_to_go"),
    ];

    public static bool IsCaution(uint flags) => (flags & (SimValues.FlagCaution | SimValues.FlagCautionWaving)) != 0;

    /// <summary>Normalised flags in a fixed order: green, caution, then the others in vocabulary order.</summary>
    public static IReadOnlyList<string> Normalize(uint flags, int sessionState)
    {
        var result = new List<string>(4);
        var stopped = (flags & (SimValues.FlagRed | SimValues.FlagCheckered)) != 0;
        if (sessionState == SimValues.StateRacing && !IsCaution(flags) && !stopped)
        {
            result.Add("green");
        }

        if (IsCaution(flags))
        {
            result.Add("caution");
        }

        foreach (var (bit, name) in Direct)
        {
            if ((flags & bit) != 0)
            {
                result.Add(name);
            }
        }

        return result;
    }
}
