using PaceLaps.Agent.Core.Commands;
using System.Globalization;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Server;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// One look at the agent for the operator: the tray app's icon, tooltip and status window, and the console's
/// <c>status</c>. Taken on any thread; it only reads.
/// </summary>
public sealed record AgentStatus
{
    public AgentState State { get; init; }

    /// <summary>Why Activate did not get to the self-test, or why the agent went back to sleep.</summary>
    public string? Problem { get; init; }

    public bool Busy { get; init; }

    public bool SimRunning { get; init; }

    public bool SimInSession { get; init; }

    /// <summary>The track of iRacing's session, if it names one.</summary>
    public string? TrackName { get; init; }

    /// <summary>Cars racing in iRacing's session (no spectators, no pace car).</summary>
    public int CarCount { get; init; }

    /// <summary>The setting: commands typed without bringing iRacing to the front.</summary>
    public bool TypedWithoutFocus { get; init; } = true;

    public Uri? Server { get; init; }

    /// <summary>The agent id from pairing; null when this PC is not paired with <see cref="Server"/>.</summary>
    public string? AgentId { get; init; }

    public IReadOnlyList<string> SettingsErrors { get; init; } = [];

    /// <summary>Null while there is no connection (Sleeping).</summary>
    public bool? ServerOnline { get; init; }

    public bool? Realtime { get; init; }

    public string? SessionName { get; init; }

    public string? NotActiveReason { get; init; }

    public bool IsExecutor { get; init; }

    public long Epoch { get; init; }

    public ChannelSet? Channels { get; init; }

    public TimeSpan? ClockOffset { get; init; }

    public int EventsBuffered { get; init; }

    public SelfTestReport? LastSelfTest { get; init; }

    public DateTimeOffset? LastSelfTestAt { get; init; }

    /// <summary>The commands finished lately, newest first.</summary>
    public IReadOnlyList<FinishedCommand> RecentCommands { get; init; } = [];

    public LocalFailsafeSettings LocalFailsafe { get; init; } = new();

    public string? LocalFailsafeHeld { get; init; }

    public DateTimeOffset? LocalFailsafeSince { get; init; }

    public bool Paired => AgentId is not null;

    public static AgentStatus Of(AgentRuntime runtime, AgentConfig settings, IReadOnlyList<string> settingsErrors, AgentCredentials? credentials, Uri? server)
    {
        var host = runtime.Host;
        var connection = runtime.Connection;
        var sim = runtime.Parts.Sim;
        return new AgentStatus
        {
            State = runtime.State,
            Problem = runtime.Problem ?? runtime.LastFailure,
            Busy = runtime.Busy,
            SimRunning = sim.IsConnected,
            SimInSession = sim.Session is not null,
            TrackName = sim.Session?.Track?.Name,
            CarCount = sim.Session is { } session ? session.Cars.Count(c => !c.IsSpectator && c.CarIdx != session.PaceCarIdx) : 0,
            TypedWithoutFocus = (runtime.Settings ?? settings).TypedWithoutFocus,
            Server = server,
            AgentId = credentials is not null && server is not null && ServerAddress.Same(credentials.Server, server) ? credentials.AgentId : null,
            SettingsErrors = settingsErrors,
            ServerOnline = connection?.IsOnline,
            Realtime = connection?.IsRealtime,
            SessionName = host?.LastView is { Active: true } view ? view.SessionName ?? view.SessionId : null,
            NotActiveReason = host?.LastView is { Active: false, Reason: { } why } ? why : null,
            IsExecutor = host?.IsExecutor == true,
            Epoch = host?.Executor.Epoch ?? 0,
            Channels = host?.Channels,
            ClockOffset = host?.ClockOffset,
            EventsBuffered = host?.Events.Count ?? 0,
            LastSelfTest = runtime.LastSelfTest,
            LastSelfTestAt = runtime.LastSelfTestAt,
            RecentCommands = runtime.RecentCommands.Items(),
            LocalFailsafe = runtime.Settings?.LocalFailsafe ?? settings.LocalFailsafe,
            LocalFailsafeHeld = host?.LocalFailsafeCopies.Held is { } held ? $"{held.Procedure} v{held.Version}" : null,
            LocalFailsafeSince = host?.LocalFailsafe.Since,
        };
    }

    /// <summary>The status as lines of text, the same in the console and the status window.</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"State: {State}{(Busy ? " (working...)" : "")}",
        };
        if (Problem is { } problem)
        {
            lines.Add($"Problem: {problem}");
        }

        lines.Add($"iRacing: {(SimInSession ? "in a session" : SimRunning ? "running, not in a session" : "not running")}");
        lines.Add($"Server: {Server?.ToString() ?? "none set"}, {(Paired ? $"paired as {AgentId}" : "not paired")}" +
            (ServerOnline is { } online ? $", {(online ? "reachable" : "UNREACHABLE")}, commands {(Realtime == true ? "live (websocket)" : "by long-poll")}" : ""));
        if (SettingsErrors.Count > 0)
        {
            lines.Add("Settings file errors: " + string.Join("; ", SettingsErrors));
        }

        if (State is AgentState.Ready or AgentState.Active)
        {
            lines.Add(SessionName is { } session ? $"Session: {session}" : $"Session: none{(NotActiveReason is { } why ? $" ({why})" : "")}");
            lines.Add($"Executor: {(IsExecutor ? "yes" : "no")}, epoch {Epoch}");
            lines.Add($"Channels: typed {Channels?.Typed}, macro {Channels?.Macro}, replay {Channels?.Sdk}");
            lines.Add($"Clock offset: {ClockOffset?.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms, events buffered: {EventsBuffered}");
        }

        lines.Add($"Local failsafe: {(LocalFailsafe.Enabled ? $"on (after {LocalFailsafe.DelaySeconds} s)" : "off")}" +
            (LocalFailsafeHeld is { } held ? $", holds {held}" : "") +
            (LocalFailsafeSince is { } since ? $", RUNNING since {since.ToLocalTime():HH:mm:ss}" : ""));
        return lines;
    }

    /// <summary>The self-test's steps, one line each.</summary>
    public static IEnumerable<string> DescribeSteps(SelfTestReport report) =>
        report.Steps.Select(s => $"{(s.Passed ? "ok  " : "FAIL")} {s.Number} {s.Name}: {s.Detail}");
}
