using PaceLaps.Agent.Core.Contracts;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>The tray icon's colour: grey, blue, amber, green, red.</summary>
public enum TrayIcon
{
    Sleeping,
    Busy,
    Ready,
    Active,
    Problem,
}

/// <summary>What the tray shows for one status: the icon, its tooltip, and which menu items work now.</summary>
public sealed record TrayView(TrayIcon Icon, string Tooltip, bool CanActivate, bool CanSleep, bool CanPair);

/// <summary>A Windows notification (it does not take the focus from iRacing).</summary>
public sealed record TrayNotice(string Title, string Text, bool Warning);

/// <summary>Turns the agent's status into what the tray app shows. No WinForms here, so it is unit-tested.</summary>
public static class TrayPresenter
{
    /// <summary>Windows cuts a tray tooltip at 127 characters.</summary>
    public const int MaxTooltip = 127;

    public static TrayView Present(AgentStatus s)
    {
        var busy = s.Busy || s.State == AgentState.SelfTest;
        var icon = Problem(s) is not null ? TrayIcon.Problem
            : busy ? TrayIcon.Busy
            : s.State switch
            {
                AgentState.Active => TrayIcon.Active,
                AgentState.Ready => TrayIcon.Ready,
                _ => TrayIcon.Sleeping,
            };
        var detail = Problem(s) ?? s.State switch
        {
            AgentState.Active => (s.IsExecutor ? "executor" : s.Channels?.Typed == ChannelReadiness.Disabled ? "view only (replays)" : "observer") + (s.SessionName is { } name ? $", {name}" : ""),
            AgentState.Ready => s.NotActiveReason ?? "no session yet",
            AgentState.SelfTest => "self-test running",
            _ => s.Paired ? "Activate to run the self-test" : "not paired yet",
        };
        var tooltip = $"PaceLaps Race Control: {s.State}, {detail}";
        return new TrayView(icon, tooltip.Length <= MaxTooltip ? tooltip : tooltip[..(MaxTooltip - 3)] + "...",
            CanActivate: s.State == AgentState.Sleeping && !busy,
            CanSleep: s.State != AgentState.Sleeping,
            CanPair: s.State == AgentState.Sleeping && !busy);
    }

    /// <summary>What changed between two looks that the operator should hear about while iRacing has the screen.</summary>
    public static IReadOnlyList<TrayNotice> Notices(AgentStatus? before, AgentStatus now)
    {
        if (before is null)
        {
            return [];
        }

        var notices = new List<TrayNotice>();
        if (now.LastSelfTest is { } report && !ReferenceEquals(report, before.LastSelfTest))
        {
            notices.Add(report.Passed
                ? new TrayNotice("Self-test passed", "The agent is ready.", false)
                : new TrayNotice("Self-test failed", report.Steps.FirstOrDefault(x => !x.Passed) is { } step ? $"Step {step.Number} {step.Name}: {step.Detail}" : "See the status window.", true));
        }
        else if (now.State == AgentState.Sleeping && now.Problem is { } problem && problem != before.Problem)
        {
            notices.Add(new TrayNotice("Not activated", problem, true));
        }

        if (now.IsExecutor && !before.IsExecutor)
        {
            notices.Add(new TrayNotice("Executor", $"This agent sends the commands{(now.SessionName is { } s ? $" for {s}" : "")}.", false));
        }
        else if (!now.IsExecutor && before.IsExecutor && now.State is AgentState.Ready or AgentState.Active)
        {
            notices.Add(new TrayNotice("No longer the executor", "Another agent sends the commands now.", false));
        }

        if (before.ServerOnline == true && now.ServerOnline == false)
        {
            notices.Add(new TrayNotice("Server unreachable", "Events are sent once it's back.", true));
        }
        else if (before.ServerOnline == false && now.ServerOnline == true)
        {
            notices.Add(new TrayNotice("Server reachable again", "Sending buffered events.", false));
        }

        if (now.Channels?.Typed == ChannelReadiness.Failed && before.Channels?.Typed != ChannelReadiness.Failed)
        {
            notices.Add(new TrayNotice("Typing into iRacing failed", "Another agent takes over. Run the self-test again.", true));
        }

        if (now.LocalFailsafeSince is not null && before.LocalFailsafeSince is null)
        {
            notices.Add(new TrayNotice("Local failsafe started", $"No server: running {now.LocalFailsafeHeld}. A steward releases it in race control.", true));
        }

        return notices;
    }

    /// <summary>What is wrong right now, if anything: shown red.</summary>
    private static string? Problem(AgentStatus s) => s switch
    {
        { LocalFailsafeSince: not null } => "LOCAL FAILSAFE RUNNING",
        { State: AgentState.Ready or AgentState.Active, ServerOnline: false } => "server unreachable",
        { State: AgentState.Ready or AgentState.Active, Channels.Typed: ChannelReadiness.Failed } => "typing into iRacing failed",
        { State: AgentState.Sleeping, Problem: { } p } => p,
        { SettingsErrors.Count: > 0 } => "the settings file has errors",
        _ => null,
    };
}
