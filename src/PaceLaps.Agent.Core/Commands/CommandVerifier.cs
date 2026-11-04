using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Snapshots;

namespace PaceLaps.Agent.Core.Commands;

/// <summary>A telemetry condition that shows a command took effect, and how long to wait for it.</summary>
public sealed record VerificationPlan(string Description, TimeSpan Timeout, Func<SimFrame, bool> Effect);

/// <summary>
/// Which telemetry signal verifies each command (spec: Command catalogue, "Verified by"; spike 3). Commands
/// without a signal end at the result "sent".
/// </summary>
public static class CommandVerifier
{
    /// <summary>How long to wait for a quick effect. Spike 3 saw them within one 0.1 s sample.</summary>
    public static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The restart type showed in PaceMode only minutes later (spike 3); check briefly, else report sent. Under
    /// green it cannot show at all (full agent test): then the result is "sent" at once.
    /// </summary>
    public static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Null when the command's effect cannot be seen in telemetry.</summary>
    public static VerificationPlan? For(RenderedCommand command, SimSession? session, SimFrame before)
    {
        int? carIdx = command.Car is null ? null : session?.CarByNumber(command.Car)?.CarIdx;
        bool CarHas(SimFrame f, Func<SimCarState, bool> test) => carIdx is { } i && f.Car(i) is { } s && test(s);

        switch (command.Type)
        {
            case "yellow":
                return new VerificationPlan("caution flag", QuickTimeout, f => FlagNormalizer.IsCaution(f.SessionFlags));
            case "black_flag" when carIdx is not null:
                return new VerificationPlan($"black flag on #{command.Car}", QuickTimeout, f => CarHas(f, s => (s.SessionFlags & SimValues.FlagBlack) != 0));
            case "clear" when carIdx is not null && CarHas(before, s => (s.SessionFlags & SimValues.FlagBlack) != 0):
                return new VerificationPlan($"black flag cleared on #{command.Car}", QuickTimeout, f => CarHas(f, s => (s.SessionFlags & SimValues.FlagBlack) == 0));
            case "wave_by" when carIdx is not null:
                return new VerificationPlan($"#{command.Car} waved around", QuickTimeout, f => CarHas(f, s => (s.PaceFlags & SimValues.PaceWavedAround) != 0));
            case "eol" when carIdx is not null:
                return new VerificationPlan($"#{command.Car} at end of line", QuickTimeout, f => CarHas(f, s => (s.PaceFlags & SimValues.PaceEndOfLine) != 0));
            case "restart_single" when FlagNormalizer.IsCaution(before.SessionFlags):
                return new VerificationPlan("pace mode single file restart", RestartTimeout, f => f.PaceMode == SimValues.PaceModeSingleFileRestart);
            case "restart_double" when FlagNormalizer.IsCaution(before.SessionFlags):
                return new VerificationPlan("pace mode double file restart", RestartTimeout, f => f.PaceMode == SimValues.PaceModeDoubleFileRestart);
            default:
                return null;
        }
    }

    /// <summary>
    /// A command whose effect is already in place and that the catalogue marks idempotent is not sent again:
    /// yellow "only throws if not already active".
    /// </summary>
    public static string? AlreadyInEffect(RenderedCommand command, SimFrame now) =>
        command.Type == "yellow" && FlagNormalizer.IsCaution(now.SessionFlags) ? "caution already active, not thrown again" : null;
}
