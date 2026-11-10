using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;

namespace PaceLaps.Agent.Core.SelfTest;

/// <summary>Asks the operator to confirm what the agent cannot check itself. The console or tray app implements it.</summary>
public interface IOperatorPrompt
{
    Task<bool> ConfirmAsync(string question, CancellationToken ct);
}

public sealed record SelfTestOptions
{
    /// <summary>
    /// The harmless command for the typed channel check (spike 12): !help replies to the sender and changes nothing
    /// in the race. It needs admin rights, so the reply also proves the operator can run admin commands.
    /// </summary>
    public string HarmlessCommand { get; init; } = "!help";

    /// <summary>Used when the session has no league id (not a league session). Spike 11 fallback: the operator selects the league.</summary>
    public int? LeagueOverride { get; init; }

    /// <summary>The macro channel is optional per agent and off unless slots are assigned and it is enabled.</summary>
    public bool MacroEnabled { get; init; }

    /// <summary>
    /// The SDK replay channel (spike 6 passed): ready once the sim is readable. An operator without admin rights then
    /// still passes, view only: replays, never the executor (spec-feedback 41).
    /// </summary>
    public bool ReplayEnabled { get; init; } = true;

    public int ClockPings { get; init; } = 5;

    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <param name="Detail">For the operator (console, tray app); may name local paths.</param>
/// <param name="Result">pass, fail, skipped or unverified for the server; by default from <paramref name="Passed"/>.</param>
/// <param name="Shared">What stewards see (self_test_result): short, never a name or a path. Null when there is nothing to say.</param>
public sealed record SelfTestStep(int Number, string Name, bool Passed, string Detail, SelfTestStepResult? Result = null, string? Shared = null)
{
    public SelfTestStepResult Outcome => Result ?? (Passed ? SelfTestStepResult.Pass : SelfTestStepResult.Fail);
}

/// <param name="Identity">The session identity the agent will report (league id possibly from the override).</param>
/// <param name="ReachedServer">The server answered the session check: the result goes to it (and its audit log).</param>
public sealed record SelfTestReport(bool Passed, IReadOnlyList<SelfTestStep> Steps, ChannelSet Channels, SessionIdentity? Identity, bool ReachedServer = false)
{
    private static readonly SelfTestStepName[] Names =
        [SelfTestStepName.Sim, SelfTestStepName.Session, SelfTestStepName.Server, SelfTestStepName.Cars, SelfTestStepName.TypedChannel, SelfTestStepName.MacroChannel];

    public string? FirstFailure => Steps.FirstOrDefault(s => !s.Passed) is { } f ? $"step {f.Number} ({f.Name}): {f.Detail}" : null;

    /// <summary>The result for the server: each step with its outcome and the shared detail only (max 200 characters).</summary>
    public SelfTestResultMessage ToContract(string agentId, DateTimeOffset at) => new()
    {
        AgentId = agentId,
        At = at,
        Passed = Passed,
        Channels = Channels,
        Steps = Steps.Select(s => new SelfTestStepReport(s.Number, Names[s.Number - 1], s.Outcome,
            s.Shared is null ? null : s.Shared.Length <= 200 ? s.Shared : s.Shared[..199] + "…")).ToList(),
    };
}

/// <summary>
/// The self-test (spec: Agent specification &gt; Self-test). The agent reports ready only when steps 1 to 5 pass.
/// Readiness is per channel: the macro channel (step 6) and the SDK channel are offered only when verified, and
/// a channel that is not verified never fails the whole test, it is just not ready.
/// </summary>
public sealed class SelfTestRunner
{
    private readonly ISimData _sim;
    private readonly IServerLink _server;
    private readonly TypedChatSender _typed;
    private readonly MacroTable _macros;
    private readonly IOperatorPrompt _operator;
    private readonly ClockOffsetEstimator _clockOffset;
    private readonly IClock _clock;
    private readonly SelfTestOptions _options;
    private readonly IMacroConfigSource? _macroConfig;

    /// <param name="macroConfig">Reads the sim's macro slots; null or unreadable falls back to the operator's confirmation.</param>
    public SelfTestRunner(ISimData sim, IServerLink server, TypedChatSender typed, MacroTable macros, IOperatorPrompt operatorPrompt,
        ClockOffsetEstimator clockOffset, IClock clock, SelfTestOptions? options = null, IMacroConfigSource? macroConfig = null)
    {
        _macroConfig = macroConfig;
        _sim = sim;
        _server = server;
        _typed = typed;
        _macros = macros;
        _operator = operatorPrompt;
        _clockOffset = clockOffset;
        _clock = clock;
        _options = options ?? new SelfTestOptions();
    }

    /// <summary>A step finished (passed, failed or skipped), as it happens: the app shows the self-test live.</summary>
    public event Action<SelfTestStep>? StepFinished;

    public async Task<SelfTestReport> RunAsync(CancellationToken ct)
    {
        var steps = new List<SelfTestStep>();
        var macro = _options.MacroEnabled && _macros.Count > 0 ? ChannelReadiness.Unverified : ChannelReadiness.Disabled;
        var typed = ChannelReadiness.Unverified;
        var sdk = ChannelReadiness.Disabled;
        SessionIdentity? identity = null;
        var reachedServer = false;

        SelfTestReport Done(bool passed) => new(passed, steps, new ChannelSet(macro, typed, sdk), identity, reachedServer);

        // 1. Sim running, telemetry readable.
        var session = _sim.Session;
        var frame = _sim.Frame;
        if (!_sim.IsConnected || session is null || frame is null)
        {
            Add(steps, new SelfTestStep(1, "sim and telemetry", false, "iRacing isn't running or telemetry is off (irsdkEnableMem=1 in app.ini)",
                Shared: "iRacing isn't running or telemetry is off"));
            return Done(false);
        }

        Add(steps, new SelfTestStep(1, "sim and telemetry", true, $"build {session.BuildVersion}"));
        sdk = _options.ReplayEnabled ? ChannelReadiness.Ready : ChannelReadiness.Disabled;

        // 2. In a session, ids read, league registered, operator is a steward.
        var leagueId = session.LeagueId > 0 ? session.LeagueId : _options.LeagueOverride ?? 0;
        // In a session means the sim reports one with a driver list. Not the ids: a hosted AI session runs on the
        // host's PC and has session id and sub-session id 0 (acceptance run, 2026-10-25).
        if (session.Cars.Count == 0 || session.SessionType == "Unknown")
        {
            Add(steps, new SelfTestStep(2, "session identity", false, "not in a session", Shared: "not in a session"));
            return Done(false);
        }

        var noIds = session.SessionId <= 0 && session.SubSessionId <= 0;

        if (leagueId <= 0)
        {
            Add(steps, new SelfTestStep(2, "session identity", false, "not a league session: select the league in the agent",
                Shared: "not a league session and no league selected"));
            return Done(false);
        }

        identity = new SessionIdentity(leagueId, session.SeasonId, session.SessionId, session.SubSessionId, frame.SessionNum, session.SessionType);
        SessionCheck check;
        try
        {
            check = await _server.CheckSessionAsync(identity, session.OperatorCustId, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Add(steps, new SelfTestStep(2, "session identity", false, "server check failed: " + e.Message));
            return Done(false);
        }

        // The server answered: from here on the result is reported to it, passed or failed.
        reachedServer = true;

        if (!check.Ok)
        {
            var why = check.Reason ?? (!check.LeagueRegistered ? $"league {leagueId} is not registered" : "the operator is not a steward of the league");
            Add(steps, new SelfTestStep(2, "session identity", false, why, Shared: why));
            return Done(false);
        }

        Add(steps, new SelfTestStep(2, "session identity", true,
            $"league {leagueId}{(session.LeagueId <= 0 ? " (selected by the operator)" : "")}, " +
            (noIds ? "no iRacing session ids (hosted AI session)" : $"sub-session {session.SubSessionId}") +
            $", session {frame.SessionNum} {session.SessionType}"));

        // 3. Server reachable, clock offset measured.
        var answered = 0;
        for (var i = 0; i < _options.ClockPings; i++)
        {
            var sentAt = _clock.UtcNow;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.PingTimeout);
            try
            {
                if (await _server.PingAsync(new ClockPingMessage { ClientSentAt = sentAt }, timeout.Token).ConfigureAwait(false) is { } pong)
                {
                    _clockOffset.Add(sentAt, pong.ServerReceivedAt, pong.ServerTime, _clock.UtcNow);
                    answered++;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // This ping timed out; try the next one.
            }
        }

        if (answered == 0)
        {
            Add(steps, new SelfTestStep(3, "server and clock", false, "the server did not answer the clock pings", Shared: "the server did not answer the clock pings"));
            return Done(false);
        }

        Add(steps, new SelfTestStep(3, "server and clock", true,
            $"{answered}/{_options.ClockPings} pings, offset {_clockOffset.Offset.TotalMilliseconds:F0} ms, round trip {_clockOffset.RoundTrip?.TotalMilliseconds:F0} ms"));

        // 4. All cars readable with the fields the sorting view needs.
        var cars = session.Cars.Where(c => !c.IsSpectator && !c.IsPaceCar).ToList();
        var inWorld = cars.Count(c => frame.Car(c.CarIdx) is { } s && s.Surface != SimValues.SurfaceNotInWorld);
        if (cars.Count == 0 || cars.Any(c => c.CarIdx < 0 || c.CarIdx >= frame.Cars.Count))
        {
            var why = cars.Count == 0 ? "no cars in the session" : "car data missing for some cars";
            Add(steps, new SelfTestStep(4, "cars", false, why, Shared: why));
            return Done(false);
        }

        Add(steps, new SelfTestStep(4, "cars", true, $"{cars.Count} cars, {inWorld} in the world"));

        // 5. Typed channel: admin rights (spike 8), the key-binding rule (spike 2), then a harmless command (spike 12).
        if (!session.DriverIsAdmin && sdk == ChannelReadiness.Ready)
        {
            // A steward's own agent beside their iRacing as a spectator: replays for the incident review only. Without
            // a typed or macro channel the server never makes it the executor.
            typed = ChannelReadiness.Disabled;
            Add(steps, new SelfTestStep(5, "typed channel", true,
                "view only: not an admin in this session (replays only)",
                SelfTestStepResult.Skipped, "view only: not an admin in this session"));
            return Done(true);
        }

        if (!session.DriverIsAdmin)
        {
            typed = ChannelReadiness.Failed;
            Add(steps, new SelfTestStep(5, "typed channel", false, "not an admin in this session",
                Shared: "not an admin in this session"));
            return Done(false);
        }

        if (!await _operator.ConfirmAsync(
                "No chat macro or sim control is on a plain key (letter, digit, # + ! space)?", ct).ConfigureAwait(false))
        {
            typed = ChannelReadiness.Failed;
            Add(steps, new SelfTestStep(5, "typed channel", false, "a macro or control may be on a plain key and would fire while typing",
                Shared: "a key binding would fire while typing"));
            return Done(false);
        }

        // The focus way (spec-feedback 37): the operator is at the PC, and it reads the chat box back.
        var sent = await _typed.SendAsync(_options.HarmlessCommand, TypedChatWay.Focus, ct).ConfigureAwait(false);
        if (!sent.Success)
        {
            typed = ChannelReadiness.Failed;
            Add(steps, new SelfTestStep(5, "typed channel", false, $"{_options.HarmlessCommand} failed: {sent.Failure} {sent.Detail}",
                Shared: $"typing {_options.HarmlessCommand} failed: {sent.Failure}"));
            return Done(false);
        }

        if (!await _operator.ConfirmAsync($"Did iRacing show the reply to {_options.HarmlessCommand} in chat?", ct).ConfigureAwait(false))
        {
            typed = ChannelReadiness.Failed;
            Add(steps, new SelfTestStep(5, "typed channel", false, $"no reply to {_options.HarmlessCommand} (admin rights?)",
                Shared: $"no reply to {_options.HarmlessCommand} in chat"));
            return Done(false);
        }

        typed = ChannelReadiness.Ready;
        Add(steps, new SelfTestStep(5, "typed channel", true, $"{_options.HarmlessCommand} in {sent.TotalTime.TotalMilliseconds:F0} ms, focus restored {sent.FocusRestored}"));

        // 6. Macro channel, if enabled: read the slot texts from the sim's configuration (spike 10); if it cannot be
        // read, the operator confirms them. Not verified means not offered, but the self-test still passes.
        var saved = macro == ChannelReadiness.Unverified ? _macroConfig?.ReadSlots() : null;
        if (saved is not null)
        {
            var wrong = _macros.Assignments
                .Where(a => !saved.TryGetValue(a.Value, out var text) || text != a.Key + "$")
                .Select(a => $"slot {a.Value} is '{(saved.TryGetValue(a.Value, out var t) ? t : "")}', expected '{a.Key}$'")
                .ToList();
            if (wrong.Count == 0)
            {
                macro = ChannelReadiness.Ready;
                Add(steps, new SelfTestStep(6, "macro channel", true, $"{_macros.Count} slot(s) match {_macroConfig!.Location}"));
            }
            else
            {
                Add(steps, new SelfTestStep(6, "macro channel", true,
                    $"not verified: {string.Join("; ", wrong)} (the sim saves macros when it closes); macros off",
                    SelfTestStepResult.Unverified, $"{wrong.Count} slot(s) do not match the sim's saved macros"));
            }
        }
        else if (macro == ChannelReadiness.Unverified)
        {
            var list = string.Join(", ", _macros.Assignments.OrderBy(a => a.Value).Select(a => $"slot {a.Value} = {a.Key}$"));
            if (await _operator.ConfirmAsync($"Do these macro slots contain exactly this text? {list}", ct).ConfigureAwait(false))
            {
                macro = ChannelReadiness.Ready;
                Add(steps, new SelfTestStep(6, "macro channel", true, $"confirmed by the operator: {list}"));
            }
            else
            {
                Add(steps, new SelfTestStep(6, "macro channel", true, "not confirmed: commands are typed",
                    SelfTestStepResult.Unverified, "the operator did not confirm the macro slots"));
            }
        }
        else
        {
            Add(steps, new SelfTestStep(6, "macro channel", true, "disabled", SelfTestStepResult.Skipped, "macro channel off"));
        }

        return Done(true);
    }

    private void Add(List<SelfTestStep> steps, SelfTestStep step)
    {
        steps.Add(step);
        StepFinished?.Invoke(step);
    }
}
