using System.Globalization;
using PaceLaps.Agent.IRacing.Sdk;

namespace PaceLaps.Agent.ConsoleHost.Spikes.Guided;

internal sealed record TelemetryChange(string Var, int Index, object? Old, object? New);

internal static class TelemetryDiff
{
    /// <summary>
    /// Discrete variables whose changes are logged. Continuous ones (lap distance, session time) are sampled
    /// separately, or every frame would be a change.
    /// </summary>
    public static readonly string[] DiscreteVars =
    [
        "SessionFlags", "SessionState", "SessionNum", "PaceMode", "PitsOpen", "SessionLapsRemainEx",
        "CarIdxSessionFlags", "CarIdxPaceFlags", "CarIdxPaceLine", "CarIdxPaceRow",
        "CarIdxOnPitRoad", "CarIdxTrackSurface", "CarIdxLapCompleted", "CarIdxPosition", "CarIdxClassPosition",
    ];

    public static readonly string[] SampledVars = ["CarIdxLapDistPct"];

    /// <summary>Changes from <paramref name="previous"/> to <paramref name="current"/>. CarIdx arrays only for indices in <paramref name="carIdxFilter"/>.</summary>
    public static IEnumerable<TelemetryChange> Diff(TelemetryFrame previous, TelemetryFrame current,
        IEnumerable<string> vars, Func<int, bool> carIdxFilter)
    {
        foreach (var name in vars)
        {
            current.Values.TryGetValue(name, out var now);
            previous.Values.TryGetValue(name, out var before);
            if (now is null)
            {
                continue;
            }

            var perCar = name.StartsWith("CarIdx", StringComparison.Ordinal);
            for (var i = 0; i < now.Length; i++)
            {
                if (perCar && !carIdxFilter(i))
                {
                    continue;
                }

                var old = before is not null && i < before.Length ? before[i] : null;
                if (!Equals(old, now[i]))
                {
                    yield return new TelemetryChange(name, i, old, now[i]);
                }
            }
        }
    }

    /// <summary>Human-readable value: flag names for bit fields, enum names for modes and surfaces.</summary>
    public static string Describe(string var, object? value)
    {
        if (value is null)
        {
            return "null";
        }

        return var switch
        {
            "SessionFlags" or "CarIdxSessionFlags" => SdkNames.SessionFlags(Convert.ToUInt32(value, CultureInfo.InvariantCulture)),
            "CarIdxPaceFlags" => SdkNames.PaceFlags(Convert.ToUInt32(value, CultureInfo.InvariantCulture)),
            "PaceMode" => SdkNames.PaceMode(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
            "CarIdxTrackSurface" => SdkNames.TrackSurface(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
            "SessionState" => SdkNames.SessionState(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
        };
    }
}
