using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>
/// The words the operator reads in the tray app: the program's name, the
/// states and roles in plain words, the self-test steps by what they check. No internal terms (epoch, channel,
/// long-poll): those stay in the status window's details and the log.
/// </summary>
public static class OperatorWords
{
    /// <summary>The program's name in its windows, menu and notifications (Race Control is the web product).</summary>
    public const string ProductName = "PaceLaps Agent";

    /// <summary>The state in a few words.</summary>
    public static string State(AgentStatus s) => s.State switch
    {
        AgentState.SelfTest => "Self-test running",
        AgentState.Ready => "Ready",
        AgentState.Active => "In a session",
        _ => "Sleeping",
    };

    /// <summary>What this agent does in its session.</summary>
    public static string Role(AgentStatus s) =>
        s.IsExecutor ? "sends the commands" : s.Channels?.Typed == ChannelReadiness.Disabled ? "view only (replays)" : "standby";

    /// <summary>What a self-test step checks, by its number.</summary>
    public static string StepTitle(int number) => number switch
    {
        1 => "iRacing",
        2 => "Session",
        3 => "Race Control",
        4 => "Cars",
        5 => "Typing into iRacing",
        6 => "Chat macros",
        _ => $"Step {number}",
    };

    /// <summary>One step as a line: what it checks and what came out.</summary>
    public static string Step(SelfTestStep step) => $"{StepTitle(step.Number)}: {step.Detail}";
}
