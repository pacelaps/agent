using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Updates;

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
            AgentState.Active => OperatorWords.Role(s) + (s.SessionName is { } name ? $" ({name})" : ""),
            AgentState.Ready => s.NotActiveReason ?? "waiting for a session",
            AgentState.SelfTest => "answer its questions",
            _ => s.Paired ? "Activate in your session" : "not paired yet",
        };
        var tooltip = $"{OperatorWords.ProductName}: {OperatorWords.State(s)}, {detail}";
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
                ? new TrayNotice("Self-test passed", "Ready. The agent joins your session by itself.", false)
                : new TrayNotice("Self-test failed", report.Steps.FirstOrDefault(x => !x.Passed) is { } step ? OperatorWords.Step(step) : "See the status window.", true));
        }
        else if (now.State == AgentState.Sleeping && now.Problem is { } problem && problem != before.Problem)
        {
            notices.Add(new TrayNotice("Not activated", problem, true));
        }

        if (now.IsExecutor && !before.IsExecutor)
        {
            notices.Add(new TrayNotice("You send the commands", $"This agent types race control's commands{(now.SessionName is { } s ? $" for {s}" : "")}.", false));
        }
        else if (!now.IsExecutor && before.IsExecutor && now.State is AgentState.Ready or AgentState.Active)
        {
            notices.Add(new TrayNotice("Standby", "Another agent sends the commands now.", false));
        }

        if (before.ServerOnline == true && now.ServerOnline == false)
        {
            notices.Add(new TrayNotice("Can't reach Race Control", "What happens in the race is sent once it's back.", true));
        }
        else if (before.ServerOnline == false && now.ServerOnline == true)
        {
            notices.Add(new TrayNotice("Race Control is back", "Sending what happened meanwhile.", false));
        }

        if (now.Channels?.Typed == ChannelReadiness.Failed && before.Channels?.Typed != ChannelReadiness.Failed)
        {
            notices.Add(new TrayNotice("Typing into iRacing failed", "Another agent takes over. Sleep, then Activate to test again.", true));
        }

        if (now.LocalFailsafeSince is not null && before.LocalFailsafeSince is null)
        {
            notices.Add(new TrayNotice("Local failsafe started", $"No Race Control: running {now.LocalFailsafeHeld}. A steward ends it in race control.", true));
        }

        return notices;
    }

    /// <summary>The menu's update line, or null while there is no newer release.</summary>
    public static string? UpdateMenuText(UpdateOffer? offer) => offer is null ? null : $"Update available: {offer.Version}...";

    /// <summary>
    /// The notification for a newer release: once per version, and never while the agent is Active or running its
    /// self-test (a race is on); it comes when the agent is Ready or Sleeping again.
    /// </summary>
    public static TrayNotice? UpdateNotice(UpdateOffer? offer, string? alreadyShown, AgentStatus now) =>
        offer is null || offer.Version == alreadyShown || now.State is AgentState.Active or AgentState.SelfTest
            ? null
            : new TrayNotice("Update available", $"PaceLaps Agent {offer.Version} is available. Open the menu to see what's new.", false);

    /// <summary>What is wrong right now, if anything: shown red.</summary>
    private static string? Problem(AgentStatus s) => s switch
    {
        { LocalFailsafeSince: not null } => "local failsafe running",
        { State: AgentState.Ready or AgentState.Active, ServerOnline: false } => "can't reach Race Control",
        { State: AgentState.Ready or AgentState.Active, Channels.Typed: ChannelReadiness.Failed } => "typing into iRacing failed",
        { State: AgentState.Sleeping, Problem: { } p } => p,
        { SettingsErrors.Count: > 0 } => "the settings file has errors",
        _ => null,
    };
}
