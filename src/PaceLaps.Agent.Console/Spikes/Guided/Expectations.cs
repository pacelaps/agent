using System.Globalization;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes.Guided;

/// <summary>Cars a step refers to, as indices into the CarIdx arrays.</summary>
internal sealed record ExpectationTargets(int? CarIdx, int PaceCarIdx);

internal enum ExpectationOutcome
{
    /// <summary>False before the command, true at some point after it.</summary>
    Verified,

    /// <summary>Already true before the command, so the command's effect cannot be told apart.</summary>
    AlreadyTrue,

    NotObserved,

    /// <summary>The variable is not broadcast, or the target car is unknown.</summary>
    Unavailable,
}

/// <summary>
/// Telemetry checks that show a command's effect (the "verified" result of the status checklist).
/// They are hints for the spike: the operator's observation decides.
/// </summary>
internal static class Expectations
{
    /// <summary>Gap in lap fraction between pace car and leader below which the pace car counts as leading the field.</summary>
    public const double PickupGap = 0.05;

    public static readonly string[] Known =
    [
        "caution", "green", "carBlack", "carBlackCleared", "carWavedAround", "carEndOfLine",
        "paceModeSingle", "paceModeDouble", "pitsClosed", "pitsOpen", "paceCarAheadOfLeader",
        "oneLapToGreen", "cautionExtended",
    ];

    /// <summary>Kinds that refer to the step's target car.</summary>
    public static bool IsPerCar(string kind) => kind.StartsWith("car", StringComparison.Ordinal);

    /// <summary>Whether the condition holds in the frame; null if it cannot be evaluated.</summary>
    public static bool? Holds(string kind, TelemetryFrame f, ExpectationTargets t) => kind switch
    {
        "caution" => Bits(f, "SessionFlags", 0) is { } s ? (s & (SdkNames.FlagCaution | SdkNames.FlagCautionWaving)) != 0 : null,
        "oneLapToGreen" => Bits(f, "SessionFlags", 0) is { } s ? (s & SdkNames.FlagOneLapToGreen) != 0 : null,

        // Still under caution but no longer at one lap to go: a command sent at one to go extended the caution.
        "cautionExtended" => Bits(f, "SessionFlags", 0) is { } s
            ? (s & (SdkNames.FlagCaution | SdkNames.FlagCautionWaving)) != 0 && (s & SdkNames.FlagOneLapToGreen) == 0
            : null,
        // Not the green bit: spike 3 showed it is set only for a few seconds after the green flag.
        "green" => Bits(f, "SessionFlags", 0) is { } s
            ? (s & (SdkNames.FlagCaution | SdkNames.FlagCautionWaving | SdkNames.FlagRed | SdkNames.FlagCheckered)) == 0
            : null,
        "carBlack" => CarBits(f, "CarIdxSessionFlags", t) is { } b ? (b & SdkNames.FlagBlack) != 0 : null,
        "carBlackCleared" => CarBits(f, "CarIdxSessionFlags", t) is { } b ? (b & SdkNames.FlagBlack) == 0 : null,
        "carWavedAround" => CarBits(f, "CarIdxPaceFlags", t) is { } b ? (b & SdkNames.PaceWavedAround) != 0 : null,
        "carEndOfLine" => CarBits(f, "CarIdxPaceFlags", t) is { } b ? (b & SdkNames.PaceEndOfLine) != 0 : null,
        "paceModeSingle" => Int(f, "PaceMode", 0) is { } m ? m == SdkNames.PaceModeSingleFileRestart : null,
        "paceModeDouble" => Int(f, "PaceMode", 0) is { } m ? m == SdkNames.PaceModeDoubleFileRestart : null,
        "pitsClosed" => f.Get("PitsOpen") is bool open ? !open : null,
        "pitsOpen" => f.Get("PitsOpen") is bool open ? open : null,
        "paceCarAheadOfLeader" => PaceCarGap(f, t.PaceCarIdx) is { } gap ? gap.Gap < PickupGap : null,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown expectation"),
    };

    /// <summary>The leader (overall position 1) and how far, in lap fraction, the pace car is ahead of it. Null unless both are on track.</summary>
    public static (int LeaderIdx, double Gap)? PaceCarGap(TelemetryFrame f, int paceCarIdx)
    {
        if (paceCarIdx < 0 || !f.Values.TryGetValue("CarIdxPosition", out var positions)
            || !f.Values.TryGetValue("CarIdxLapDistPct", out var dist)
            || !f.Values.TryGetValue("CarIdxTrackSurface", out var surface))
        {
            return null;
        }

        var leader = Array.FindIndex(positions, p => Convert.ToInt32(p, CultureInfo.InvariantCulture) == 1);
        if (leader < 0 || leader == paceCarIdx
            || Convert.ToInt32(surface[paceCarIdx], CultureInfo.InvariantCulture) != SdkNames.SurfaceOnTrack
            || Convert.ToInt32(surface[leader], CultureInfo.InvariantCulture) != SdkNames.SurfaceOnTrack)
        {
            return null;
        }

        var pace = Convert.ToDouble(dist[paceCarIdx], CultureInfo.InvariantCulture);
        var lead = Convert.ToDouble(dist[leader], CultureInfo.InvariantCulture);
        if (pace < 0 || lead < 0)
        {
            return null;
        }

        var gap = pace - lead;
        if (gap < 0)
        {
            gap += 1;
        }

        return (leader, gap);
    }

    private static uint? Bits(TelemetryFrame f, string name, int idx) =>
        f.Get(name, idx) is { } v ? Convert.ToUInt32(v, CultureInfo.InvariantCulture) : null;

    private static int? Int(TelemetryFrame f, string name, int idx) =>
        f.Get(name, idx) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;

    private static uint? CarBits(TelemetryFrame f, string name, ExpectationTargets t) =>
        t.CarIdx is { } idx ? Bits(f, name, idx) : null;
}

/// <summary>Follows one expectation over a step's watch window.</summary>
internal sealed class ExpectationTracker
{
    private readonly ExpectationTargets _targets;
    private readonly bool? _baseline;
    private bool _everEvaluated;

    public ExpectationTracker(string kind, TelemetryFrame baseline, ExpectationTargets targets)
    {
        Kind = kind;
        _targets = targets;
        _baseline = Expectations.Holds(kind, baseline, targets);
        _everEvaluated = _baseline is not null;
    }

    public string Kind { get; }

    /// <summary>Seconds after the command when the condition first held (only for a false baseline).</summary>
    public double? FirstSeenAfterSeconds { get; private set; }

    public ExpectationOutcome Outcome =>
        !_everEvaluated ? ExpectationOutcome.Unavailable
        : _baseline == true ? ExpectationOutcome.AlreadyTrue
        : FirstSeenAfterSeconds is not null ? ExpectationOutcome.Verified
        : ExpectationOutcome.NotObserved;

    /// <summary>Returns true the first time the condition is seen to become true.</summary>
    public bool Observe(TelemetryFrame frame, TimeSpan sinceCommand)
    {
        var holds = Expectations.Holds(Kind, frame, _targets);
        _everEvaluated |= holds is not null;
        if (holds == true && _baseline != true && FirstSeenAfterSeconds is null)
        {
            FirstSeenAfterSeconds = Math.Round(sinceCommand.TotalSeconds, 2);
            return true;
        }

        return false;
    }
}
