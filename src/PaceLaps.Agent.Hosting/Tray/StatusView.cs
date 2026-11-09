using System.Globalization;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;

namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>How a row of the status window looks: fine, worth a look, wrong, or not in use right now.</summary>
public enum StatusCheck
{
    Ok,
    Warning,
    Problem,
    Off,
}

/// <summary>The one button the header offers for what to do next.</summary>
public enum StatusAction
{
    None,
    Pair,
    Activate,
    Sleep,
}

public sealed record StatusRow(string Label, StatusCheck Check, string Text);

/// <summary>The thin strip on top of the app's state card (the WPF plan, W3): green only while all is well in a session.</summary>
public enum StatusStrip
{
    Grey,
    Blue,
    Amber,
    Green,
    Red,
    Hazard,
}

/// <summary>One of the status screen's tiles: what it is about, how it is, in a word, and a line under it.</summary>
public sealed record StatusTile(string Label, StatusCheck Check, string Value, string Detail);

/// <summary>
/// What the status window shows (the agent polish plan, 3.3): a header in the state's colour with a sentence on what is
/// going on and the one button that fits; rows for iRacing, Race Control, the session and the pairing; the last
/// self-test step by step; the local failsafe; and, for support, the technical details. Pure, so every state is tested.
/// </summary>
public sealed record StatusView(
    TrayIcon Tone,
    string Title,
    string Sentence,
    StatusAction Action,
    IReadOnlyList<StatusRow> Rows,
    string SelfTestSummary,
    IReadOnlyList<StatusRow> SelfTestSteps,
    StatusRow LocalFailsafe,
    IReadOnlyList<string> Details)
{
    public static StatusView From(AgentStatus s)
    {
        var tone = TrayPresenter.Present(s).Icon;
        var (title, sentence, action) = HeaderOf(s);
        var (sideLabel, sideName, role) = SideOf(s);
        return new StatusView(tone, title, sentence, action, RowsOf(s), SummaryOf(s), StepsOf(s.LastSelfTest), FailsafeOf(s), DetailsOf(s))
        {
            Strip = StripOf(s, tone), Eyebrow = EyebrowOf(s), SideLabel = sideLabel, SideName = sideName, Role = role, Tiles = TilesOf(s),
        };
    }

    public StatusStrip Strip { get; init; }

    /// <summary>The small line over the title: what is live, or since when something is wrong.</summary>
    public string Eyebrow { get; init; } = "";

    /// <summary>The card's right side: a label and a name (the session, or the failsafe's procedure); null when there is none.</summary>
    public string? SideLabel { get; init; }

    public string? SideName { get; init; }

    /// <summary>The role chip under the session's name (In a session only).</summary>
    public string? Role { get; init; }

    public IReadOnlyList<StatusTile> Tiles { get; init; } = [];

    /// <summary>The label on the header's button.</summary>
    public static string ActionText(StatusAction action) => action switch
    {
        StatusAction.Pair => "Pair with Race Control...",
        StatusAction.Activate => "Activate",
        StatusAction.Sleep => "Sleep",
        _ => "",
    };

    private static (string Title, string Sentence, StatusAction Action) HeaderOf(AgentStatus s)
    {
        var connected = s.State is AgentState.Ready or AgentState.Active;
        return s switch
        {
            { LocalFailsafeSince: not null } => ("Local failsafe running",
                "Race Control can't be reached, so the agent keeps the caution going by itself. A steward ends it in race control.", StatusAction.None),
            _ when connected && s.ServerOnline == false => ("Can't reach Race Control",
                "The agent keeps trying. What happens in the race is sent once Race Control is back.", StatusAction.None),
            _ when connected && s.Channels?.Typed == ChannelReadiness.Failed => ("Typing into iRacing failed",
                "Another agent sends the commands now. Sleep, then Activate to run the self-test again.", StatusAction.Sleep),
            { Busy: true } or { State: AgentState.SelfTest } => ("Self-test running",
                "Answer its questions. iRacing comes to the front for a moment.", StatusAction.None),
            { State: AgentState.Sleeping, Paired: false } => ("Not paired yet",
                "Pair this PC with Race Control once. You need a steward account in your league.", StatusAction.Pair),
            { State: AgentState.Sleeping, Problem: { } problem } => ("Not activated", problem, StatusAction.Activate),
            { State: AgentState.Sleeping, SettingsErrors.Count: > 0 } => ("The settings file has errors",
                "The agent uses the defaults for those settings. Open Settings to fix them.", StatusAction.None),
            { State: AgentState.Sleeping } => ("Sleeping",
                "Join your session in iRacing as an admin, then Activate. While it sleeps, the agent doesn't talk to Race Control.", StatusAction.Activate),
            { State: AgentState.Ready } => ("Ready",
                s.NotActiveReason ?? "Waiting for a session. The agent joins by itself once Race Control has one for your iRacing session.", StatusAction.Sleep),
            _ => ("In a session", RoleSentence(s), StatusAction.Sleep),
        };
    }

    private static string RoleSentence(AgentStatus s)
    {
        var session = s.SessionName is { } name ? $" for {name}" : "";
        return s.IsExecutor ? $"This agent types race control's commands into iRacing{session}. You can drive; iRacing only comes to the front for the rare retry."
            : s.Channels?.Typed == ChannelReadiness.Disabled ? $"View only{session}: stewards can play incident replays here. This agent never sends commands."
            : $"Standby{session}: another agent sends the commands, and this one takes over if it drops out.";
    }

    private static StatusStrip StripOf(AgentStatus s, TrayIcon tone) => s switch
    {
        { LocalFailsafeSince: not null } => StatusStrip.Hazard,
        _ when tone == TrayIcon.Problem => StatusStrip.Red,
        _ when tone == TrayIcon.Busy => StatusStrip.Blue,
        { State: AgentState.Active } => StatusStrip.Green,
        { State: AgentState.Ready } => StatusStrip.Amber,
        _ => StatusStrip.Grey,
    };

    private static string EyebrowOf(AgentStatus s)
    {
        var connected = s.State is AgentState.Ready or AgentState.Active;
        return s switch
        {
            { LocalFailsafeSince: { } since } => $"Race Control lost at {since.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}",
            _ when connected && s.ServerOnline == false => "Retrying",
            { Busy: true } or { State: AgentState.SelfTest } => "Self-test",
            _ when connected => "Live" + (s.TrackName is { } track ? $" · {track}" : ""),
            { Paired: false } => "First start",
            _ => "Asleep · not connected to Race Control",
        };
    }

    private static (string? Label, string? Name, string? Role) SideOf(AgentStatus s) => s switch
    {
        { LocalFailsafeSince: not null } => ("Running", s.LocalFailsafeHeld ?? "Local failsafe", null),
        { State: AgentState.Active } => ("Session", s.SessionName ?? "Joined", OperatorWords.Role(s) switch
        {
            "sends the commands" => "Sends the commands",
            "standby" => "Standby",
            var other => char.ToUpperInvariant(other[0]) + other[1..],
        }),
        { State: AgentState.Ready } => ("Session", "None yet", null),
        _ => (null, null, null),
    };

    private static List<StatusTile> TilesOf(AgentStatus s)
    {
        var connected = s.State is AgentState.Ready or AgentState.Active;
        var viewOnly = s.Channels?.Typed == ChannelReadiness.Disabled;
        var cars = s.CarCount > 0 ? $"{s.CarCount} cars" : "";
        return
        [
            s.SimInSession ? new StatusTile("iRacing", StatusCheck.Ok, "In the session", connected ? Join(viewOnly ? "View only" : "Admin rights", cars) : cars)
                : s.SimRunning ? new StatusTile("iRacing", connected ? StatusCheck.Warning : StatusCheck.Off, "Running", "Not in a session")
                : new StatusTile("iRacing", connected ? StatusCheck.Problem : StatusCheck.Off, "Not running", "Start iRacing and join your session"),
            !connected || s.ServerOnline is null ? new StatusTile("Race Control", StatusCheck.Off, s.State == AgentState.SelfTest ? "Connecting" : "Asleep", "Not connected while sleeping")
                : s.ServerOnline == true ? new StatusTile("Race Control", StatusCheck.Ok, "Connected", s.Realtime == true ? "Live updates" : "Updates by polling")
                : new StatusTile("Race Control", StatusCheck.Problem, "Can't reach it", s.EventsBuffered > 0 ? $"Retrying · {s.EventsBuffered} events waiting" : "Retrying"),
            !connected ? new StatusTile("Typing", StatusCheck.Off, "Not tested", "The self-test checks it")
                : viewOnly ? new StatusTile("Typing", StatusCheck.Off, "Off", "No admin rights: replays only")
                : s.Channels?.Typed == ChannelReadiness.Failed ? new StatusTile("Typing", StatusCheck.Problem, "Failed", "Sleep, then Activate to test again")
                : new StatusTile("Typing", StatusCheck.Ok, "Ready", s.TypedWithoutFocus ? "Without taking focus" : "Brings iRacing to the front"),
            s.LocalFailsafeSince is { } since ? new StatusTile("Local failsafe", StatusCheck.Problem, "Running", $"Since {since.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}")
                : !s.LocalFailsafe.Enabled ? new StatusTile("Local failsafe", StatusCheck.Off, "Off", "Turn it on in the settings")
                : new StatusTile("Local failsafe", connected && s.LocalFailsafeHeld is null && s.State == AgentState.Active ? StatusCheck.Warning : StatusCheck.Ok,
                    connected ? "Armed" : "On", $"After {s.LocalFailsafe.DelaySeconds} s without Race Control"),
        ];
    }

    private static string Join(string a, string b) => b.Length == 0 ? a : $"{a} · {b}";

    private static List<StatusRow> RowsOf(AgentStatus s)
    {
        var connected = s.State is AgentState.Ready or AgentState.Active;
        var rows = new List<StatusRow>
        {
            s.SimInSession ? new StatusRow("iRacing", StatusCheck.Ok, "In a session")
                : s.SimRunning ? new StatusRow("iRacing", connected ? StatusCheck.Warning : StatusCheck.Off, "Running, not in a session")
                : new StatusRow("iRacing", connected ? StatusCheck.Problem : StatusCheck.Off, "Not running"),
            !connected || s.ServerOnline is null ? new StatusRow("Race Control", StatusCheck.Off, s.State == AgentState.SelfTest ? "Connecting" : "Not connected while sleeping")
                : s.ServerOnline == true ? new StatusRow("Race Control", StatusCheck.Ok, "Connected")
                : new StatusRow("Race Control", StatusCheck.Problem, "Can't reach it, retrying"),
            s.State == AgentState.Active
                ? new StatusRow("Session", s.Channels?.Typed == ChannelReadiness.Failed ? StatusCheck.Problem : StatusCheck.Ok,
                    (s.SessionName ?? "Joined") + ": " + OperatorWords.Role(s))
                : new StatusRow("Session", StatusCheck.Off, s.State == AgentState.Ready ? "None yet" : "None"),
            s.Paired ? new StatusRow("Pairing", StatusCheck.Ok, s.Server is { } server ? $"Paired with {server.Host}" : "Paired")
                : new StatusRow("Pairing", StatusCheck.Problem, "Not paired yet"),
        };
        return rows;
    }

    private static string SummaryOf(AgentStatus s)
    {
        var at = s.LastSelfTestAt is { } time ? $" at {time.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}" : "";
        return s.LastSelfTest switch
        {
            null => "Not run yet. Activate runs it.",
            { Passed: true } => $"Passed{at}.",
            _ => $"Failed{at}. The agent went back to sleep.",
        };
    }

    private static List<StatusRow> StepsOf(SelfTestReport? report) => report is null ? [] : report.Steps
        .Select(step => new StatusRow(OperatorWords.StepTitle(step.Number), step.Outcome switch
        {
            SelfTestStepResult.Pass => StatusCheck.Ok,
            SelfTestStepResult.Fail => StatusCheck.Problem,
            SelfTestStepResult.Unverified => StatusCheck.Warning,
            _ => StatusCheck.Off,
        }, step.Detail))
        .ToList();

    private static StatusRow FailsafeOf(AgentStatus s)
    {
        const string label = "Local failsafe";
        if (s.LocalFailsafeSince is { } since)
        {
            return new StatusRow(label, StatusCheck.Problem, $"Running since {since.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}. A steward ends it in race control.");
        }

        if (!s.LocalFailsafe.Enabled)
        {
            return new StatusRow(label, StatusCheck.Off, "Off");
        }

        var after = $"starts after {s.LocalFailsafe.DelaySeconds} s without Race Control";
        return s.LocalFailsafeHeld is { } held ? new StatusRow(label, StatusCheck.Ok, $"On: {after} ({held})")
            : s.State == AgentState.Active ? new StatusRow(label, StatusCheck.Warning, $"On, but the league's procedure isn't copied yet: {after}")
            : new StatusRow(label, StatusCheck.Ok, $"On: {after}");
    }

    private static List<string> DetailsOf(AgentStatus s)
    {
        string Channel(ChannelReadiness? c) => c switch
        {
            ChannelReadiness.Ready => "ready",
            ChannelReadiness.Failed => "failed",
            ChannelReadiness.Unverified => "not verified",
            ChannelReadiness.Disabled => "off",
            _ => "unknown",
        };

        var lines = new List<string>
        {
            $"Server: {s.Server?.ToString() ?? "none set"}",
            $"Agent id: {s.AgentId ?? "not paired"}",
        };
        if (s.State is AgentState.Ready or AgentState.Active)
        {
            lines.Add($"Typing into iRacing: {Channel(s.Channels?.Typed)}; chat macros: {Channel(s.Channels?.Macro)}; replays: {Channel(s.Channels?.Sdk)}");
            lines.Add($"Commands arrive {(s.Realtime == true ? "live (websocket)" : "by long-poll")}; executor epoch {s.Epoch}");
            lines.Add($"Clock offset to Race Control: {s.ClockOffset?.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) ?? "?"} ms; events waiting to be sent: {s.EventsBuffered}");
        }

        if (s.SettingsErrors.Count > 0)
        {
            lines.Add("Settings file: " + string.Join("; ", s.SettingsErrors));
        }

        return lines;
    }
}
