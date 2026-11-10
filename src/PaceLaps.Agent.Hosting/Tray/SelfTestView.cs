using System.Globalization;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>Where a self-test step stands: done (with its check), running now, or still to come.</summary>
public enum StepState
{
    Done,
    Running,
    Pending,
}

public sealed record SelfTestLine(int Number, string Title, StepState State, StatusCheck Check, string Text);

/// <summary>
/// The self-test screen (the design canvas's "Self-test", the WPF plan step 3): the six steps live while it runs, the
/// last result otherwise, a step bar, a line on top, a sentence, and the button to run it when that is possible. Pure.
/// </summary>
public sealed record SelfTestView(string Eyebrow, string Sentence, bool Running, bool CanRun, IReadOnlyList<SelfTestLine> Steps)
{
    public const int StepCount = 6;

    public static SelfTestView From(AgentStatus s)
    {
        var running = s.State == AgentState.SelfTest || (s.Busy && s.State == AgentState.Sleeping);
        var done = running ? s.SelfTestProgress : s.LastSelfTest?.Steps ?? [];
        var current = running ? Enumerable.Range(1, StepCount).FirstOrDefault(n => done.All(d => d.Number != n), StepCount + 1) : StepCount + 1;
        var steps = Enumerable.Range(1, StepCount).Select(n =>
            done.LastOrDefault(d => d.Number == n) is { } step ? new SelfTestLine(n, OperatorWords.StepTitle(n), StepState.Done, CheckOf(step.Outcome), step.Detail)
            : n == current ? new SelfTestLine(n, OperatorWords.StepTitle(n), StepState.Running, StatusCheck.Off, Doing(n))
            : new SelfTestLine(n, OperatorWords.StepTitle(n), StepState.Pending, StatusCheck.Off, running || s.LastSelfTest is null ? "" : "Not run")).ToList();

        var at = s.LastSelfTestAt is { } time ? time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : null;
        var (eyebrow, sentence) = s switch
        {
            _ when running => ($"Step {Math.Min(current, StepCount)} of {StepCount}", "Answer its questions. iRacing comes to the front for a moment."),
            { LastSelfTest: { Passed: true } } => (at is null ? "Passed" : $"Passed at {at}", "Everything the agent needs works."),
            { LastSelfTest: { } report } => (at is null ? "Failed" : $"Failed at {at}",
                report.Steps.FirstOrDefault(x => !x.Passed) is { } failed ? $"{OperatorWords.Step(failed)}. Fix it, then run the self-test again." : "Run the self-test again."),
            _ => ("Not run yet", "It checks iRacing, your session, Race Control, the cars and typing into the chat. Join your session in iRacing as an admin first."),
        };
        var canRun = s is { State: AgentState.Sleeping, Busy: false, Paired: true };
        if (!running && !canRun && s.State is AgentState.Ready or AgentState.Active)
        {
            sentence += " To run it again, put the agent to sleep and activate it.";
        }

        return new SelfTestView(eyebrow, sentence, running, canRun, steps)
        {
            Result = running ? StatusCheck.Off : s.LastSelfTest is { Passed: var passed } ? passed ? StatusCheck.Ok : StatusCheck.Problem : StatusCheck.Off,
        };
    }

    /// <summary>How the last run ended (Ok, Problem), Off while it runs or before the first: the line on top takes its colour.</summary>
    public StatusCheck Result { get; init; }

    private static StatusCheck CheckOf(SelfTestStepResult outcome) => outcome switch
    {
        SelfTestStepResult.Pass => StatusCheck.Ok,
        SelfTestStepResult.Fail => StatusCheck.Problem,
        SelfTestStepResult.Unverified => StatusCheck.Warning,
        _ => StatusCheck.Off,
    };

    /// <summary>What a step does while it runs.</summary>
    private static string Doing(int number) => number switch
    {
        1 => "Looking for iRacing",
        2 => "Checking the session with Race Control",
        3 => "Measuring the connection",
        4 => "Reading the cars",
        5 => "Typing !help into the chat",
        6 => "Checking the chat macros",
        _ => "",
    };
}
