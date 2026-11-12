using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Events;
using PaceLaps.Agent.Core.LocalFailsafe;
using PaceLaps.Agent.Core.SelfTest;
using PaceLaps.Agent.Core.Results;
using PaceLaps.Agent.Core.Server;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Snapshots;
using PaceLaps.Agent.Core.Timing;
using PaceLaps.Agent.Core.States;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.Agent;

public sealed record AgentHostOptions
{
    public required string AgentId { get; init; }
    public required string AgentVersion { get; init; }

    /// <summary>Snapshot rate of the executor: the spec asks for 1 to 2 Hz.</summary>
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Snapshot rate of a standby agent (active, not the executor). The server keeps its snapshots only while the
    /// executor's are missing, so they only need to bridge a handover; sending them slower saves the server work.
    /// </summary>
    public TimeSpan StandbySnapshotInterval { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often events are detected; also the loop tick.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// How often the sector crossings measured at every frame are sent, bundled in one event (race control's lap
    /// times, spec-feedback 39): about once a second, so the requests stay as they are.
    /// </summary>
    public TimeSpan CrossingsInterval { get; init; } = TimeSpan.FromSeconds(1);

    public SelfTestOptions SelfTest { get; init; } = new();

    public CommandLimits Limits { get; init; } = new();

    /// <summary>The operator's local failsafe setting (config file): reported in every heartbeat.</summary>
    public LocalFailsafeSettings LocalFailsafe { get; init; } = new();
}

/// <summary>
/// The agent's proof-of-concept core: state machine, self-test, snapshots, heartbeats, events and command
/// execution, wired to the sim and the server through ports. It has no race logic: it reports what the sim
/// shows and executes what the server sends.
/// <list type="bullet">
/// <item>Sleeping and self-test: nothing is sent (apart from what the self-test needs), and commands are ignored.</item>
/// <item>Ready: hello and heartbeats; commands are answered as failed, because only active agents execute.</item>
/// <item>Active: snapshots, events, heartbeats, and commands.</item>
/// </list>
/// The server decides Ready or Active: the hello result and every heartbeat result carry its view (bound to a
/// session, epoch, executor), and the agent follows it. Events stay buffered until acknowledged and are resent
/// after an outage.
/// </summary>
public sealed class AgentHost : IStatusSink
{
    /// <summary>
    /// Events kept until the server acknowledges them. Flags and incidents are a few hundred a race at most; the sector
    /// crossings add about one a second while cars drive, so an outage of about a quarter of an hour fits. Beyond it
    /// the oldest are dropped (the lap times have a gap) and the outage report covers it.
    /// </summary>
    public const int EventBufferCapacity = 1000;

    /// <summary>The contract's limit of crossings in one sector_crossings event.</summary>
    public const int MaxCrossingsPerEvent = 1000;

    private static readonly ChannelSet NoChannels = new(ChannelReadiness.Disabled, ChannelReadiness.Disabled, ChannelReadiness.Disabled);

    private readonly AgentHostOptions _options;
    private readonly ISimData _sim;
    private readonly IServerLink _server;
    private readonly IClock _clock;
    private readonly SelfTestRunner _selfTest;
    private readonly ClockOffsetEstimator _clockOffset = new();
    private readonly EventDetector _detector = new();
    private readonly SectorCrossings _crossings = new();
    private readonly SessionResultsWatch _results = new();
    private int? _resultsUpdate;
    private readonly object _crossingsGate = new();
    private readonly List<SectorCrossingData> _pendingCrossings = [];
    private volatile SimSession? _crossingsSession;
    private TimeSpan _nextCrossings;
    private readonly ILogger _logger;
    private TimeSpan _nextSnapshot;
    private TimeSpan _nextHeartbeat;
    private long _snapshotSeq;
    private SessionIdentity? _identity;
    private ChannelSet _verified = NoChannels;
    private bool _macrosOff;
    private bool _typedFailed;
    private long _reportedDropped;
    private TimeSpan _lastServerContact;
    private int? _rank;
    private volatile bool _leaving;

    public AgentHost(AgentHostOptions options, ISimData sim, IServerLink server, TypedChatSender typed, IMacroChannel? macro,
        MacroTable macros, IReplayChannel? replay, IOperatorPrompt operatorPrompt, IClock clock, ILoggerFactory? loggers = null,
        IMacroConfigSource? macroConfig = null, ILocalFailsafeStore? localFailsafeStore = null)
    {
        _options = options;
        _sim = sim;
        _server = server;
        _clock = clock;
        loggers ??= NullLoggerFactory.Instance;
        _logger = loggers.CreateLogger<AgentHost>();
        _selfTest = new SelfTestRunner(sim, server, typed, macros, operatorPrompt, _clockOffset, clock, options.SelfTest, macroConfig);
        Executor = new CommandExecutor(new CommandRenderer(options.Limits), typed, macro, macros, replay, sim, clock, this,
            () => Channels, () => _clockOffset.Offset, logger: loggers.CreateLogger<CommandExecutor>());
        States.Changed += (from, to, reason) => _logger.LogInformation("State {From} -> {To}: {Reason}", from, to, reason);
        Executor.TypedChannelFailed += reason =>
        {
            _typedFailed = true;
            // Told at once, so the server hands over without waiting for the next heartbeat.
            _nextHeartbeat = _clock.Elapsed;
            _logger.LogWarning("Typed channel failed ({Reason}): reported until the next self-test", reason);
        };
        Events = new EventBuffer(EventBufferCapacity, firstSeq: clock.UtcNow.ToUnixTimeMilliseconds());
        LocalFailsafeCopies = new LocalFailsafeCopies(server, localFailsafeStore ?? new MemoryLocalFailsafeStore(), clock, loggers.CreateLogger<LocalFailsafeCopies>());
        LocalFailsafe = new LocalFailsafeWatch(options.LocalFailsafe, LocalFailsafeCopies, Executor.ExecuteLocalAsync, ReportLocalFailsafe, clock,
            loggers.CreateLogger<LocalFailsafeWatch>());
        _server.CommandReceived += OnCommand;
        if (sim is ISimFrames frames)
        {
            frames.FrameArrived += OnFrameArrived;
        }

        _server.ViewReceived += view =>
        {
            if (States.State is AgentState.Ready or AgentState.Active && !_leaving)
            {
                ApplyView(view);
            }
        };
        _server.EventsAcknowledged += seq => Events.Acknowledge(seq);
        _server.Reconnected += Resync;
    }

    public AgentStateMachine States { get; } = new();

    public CommandExecutor Executor { get; }

    public EventBuffer Events { get; }

    /// <summary>
    /// The channels as the server sees them and the executor uses them: what the last self-test verified, with the
    /// macro channel off while the league's policy says so (it returns to its verified state when allowed again), and
    /// the typed channel failed once it stopped working (until the next self-test; the server then hands over).
    /// </summary>
    public ChannelSet Channels
    {
        get
        {
            var channels = _macrosOff && _verified.Macro != ChannelReadiness.Disabled ? _verified with { Macro = ChannelReadiness.Disabled } : _verified;
            return _typedFailed ? channels with { Typed = ChannelReadiness.Failed } : channels;
        }
    }

    /// <summary>The league's macros policy as the server last said it (allowed until it says otherwise).</summary>
    public MacrosPolicy MacrosPolicy => _macrosOff ? MacrosPolicy.Off : MacrosPolicy.Allowed;

    /// <summary>Each self-test step as it finishes (SelfTestRunner.StepFinished).</summary>
    public event Action<SelfTestStep>? SelfTestStepFinished
    {
        add => _selfTest.StepFinished += value;
        remove => _selfTest.StepFinished -= value;
    }

    public SelfTestReport? LastSelfTest { get; private set; }

    /// <summary>The copy of the league's local failsafe this agent holds (spec: Local failsafe).</summary>
    public LocalFailsafeCopies LocalFailsafeCopies { get; }

    /// <summary>The local failsafe's watchdog: it runs the copy when the server is gone long enough (if the operator turned it on).</summary>
    public LocalFailsafeWatch LocalFailsafe { get; }

    public bool IsExecutor { get; private set; }

    /// <summary>The server's last view of this agent; its reason says why the agent is not active.</summary>
    public ServerView? LastView { get; private set; }

    public TimeSpan ClockOffset => _clockOffset.Offset;

    /// <summary>Now on the server's clock: every time the agent sends is (spec: Connection loss and resync).</summary>
    private DateTimeOffset ServerNow => _clock.UtcNow + _clockOffset.Offset;

    /// <summary>Activate: run the self-test; if it passes, report ready and join the session on the server.</summary>
    public async Task<SelfTestReport?> ActivateAsync(CancellationToken ct)
    {
        if (!States.Activate())
        {
            return null;
        }

        var report = await _selfTest.RunAsync(ct).ConfigureAwait(false);
        LastSelfTest = report;
        _typedFailed = false;
        if (report.ReachedServer)
        {
            // Every self-test the server answered goes to its audit log, passed or failed: the test's last message,
            // queued before a failed one returns the agent to sleeping.
            _server.Send(report.ToContract(_options.AgentId, _clock.UtcNow));
        }
        if (!report.Passed)
        {
            _verified = NoChannels;
            States.SelfTestFailed(report.FirstFailure ?? "unknown");
            return report;
        }

        _verified = report.Channels;
        _identity = report.Identity;
        States.SelfTestPassed();
        await SendHelloAsync(ct).ConfigureAwait(false);
        return report;
    }

    /// <summary>
    /// Follows the server's view: bound to a session means Active, not bound means Ready (a hosted session waits
    /// until a steward opens the league session; the heartbeat result then says Active).
    /// </summary>
    public void ApplyView(ServerView view)
    {
        // The server answered: the local failsafe's clock starts again, and the copy follows what it names.
        _lastServerContact = _clock.Elapsed;
        LocalFailsafe.ServerBack();
        _rank = view.LocalFailsafeRank ?? _rank;
        if (view.ReleaseLocalFailsafe)
        {
            LocalFailsafe.Release();
        }

        _ = LocalFailsafeCopies.FollowAsync(view, CancellationToken.None);
        var previous = LastView;
        if (previous is not null && view.Epoch < Executor.Epoch && (view.SessionId is null || view.SessionId == previous.SessionId))
        {
            // An answer that crossed a newer executor assignment (full agent test): the newer epoch stands.
            _logger.LogDebug("Ignored executor epoch {Epoch}: {Current} is newer", view.Epoch, Executor.Epoch);
            view = view with { Epoch = Executor.Epoch, IsExecutor = IsExecutor };
        }
        LastView = view.Active is null && previous is not null ? previous with { Epoch = view.Epoch, IsExecutor = view.IsExecutor } : view;
        if (view.Active == true && States.Joined())
        {
            _nextSnapshot = _nextHeartbeat = _clock.Elapsed;
        }
        else if (view.Active == false && States.Left(view.Reason ?? "the server released the agent"))
        {
            Executor.DropQueued();
        }
        else if (view.Active == false && view.Reason is not null && view.Reason != previous?.Reason)
        {
            _logger.LogInformation("Not active: {Reason}", view.Reason);
        }

        if (view.Epoch != Executor.Epoch || view.IsExecutor != IsExecutor)
        {
            AssignExecutor(view.Epoch, view.IsExecutor);
        }

        // The league's macros policy (spec: Command channels): off means every command is typed.
        if (view.Macros is { } policy && (policy == MacrosPolicy.Off) != _macrosOff)
        {
            _macrosOff = policy == MacrosPolicy.Off;
            _logger.LogInformation("The league's macros policy is {Policy}: macro channel {Channel}", policy, Channels.Macro);
        }
    }

    /// <summary>The server assigned a new executor epoch (spec: Executor model).</summary>
    public void AssignExecutor(long epoch, bool isExecutor)
    {
        Executor.Epoch = epoch;
        if (isExecutor && !IsExecutor)
        {
            // From standby rate to the executor's at once: the next snapshot goes out on the next tick.
            _nextSnapshot = _clock.Elapsed;
        }

        IsExecutor = isExecutor;
        _logger.LogInformation("Executor epoch {Epoch}, executor {IsExecutor}", epoch, isExecutor);
    }

    /// <summary>
    /// Before Sleep, from Ready or Active: one last heartbeat that says so, so the server releases the agent at once
    /// instead of counting it lost (spec: States). Views that arrive meanwhile are ignored. False when it did not get
    /// through in time; Sleep goes ahead either way.
    /// </summary>
    public async Task<bool> SayGoodbyeAsync(TimeSpan timeout)
    {
        if (States.State is not (AgentState.Ready or AgentState.Active))
        {
            return false;
        }

        _leaving = true;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await _server.SendNowAsync(new HeartbeatMessage
            {
                AgentId = _options.AgentId,
                Epoch = Executor.Epoch,
                State = AgentState.Sleeping,
                InSession = false,
            }, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Back to sleep: stop all traffic and drop queued commands.</summary>
    public void Sleep()
    {
        _leaving = false;
        if (States.Sleep())
        {
            LocalFailsafe.Stop();
            _verified = NoChannels;
            IsExecutor = false;
            Executor.DropQueued();
        }
    }

    /// <summary>Runs the agent until cancelled: the loop and the command queue.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var queue = Executor.RunAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Tick();
                await _clock.Delay(_options.Tick, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        await queue.ConfigureAwait(false);
    }

    /// <summary>One loop iteration: heartbeat, events and snapshot when due. Public for tests.</summary>
    public void Tick()
    {
        var state = States.State;
        if (state is AgentState.Sleeping or AgentState.SelfTest)
        {
            return;
        }

        var now = _clock.Elapsed;
        if (now >= _nextHeartbeat)
        {
            _nextHeartbeat = now + _options.HeartbeatInterval;
            _server.Send(new HeartbeatMessage
            {
                AgentId = _options.AgentId,
                Epoch = Executor.Epoch,
                State = state,
                InSession = _sim.IsConnected && _sim.Session is not null,
                Channels = Channels,
                LastCommandLatencyMs = Executor.LastLatencyMs,
                LocalFailsafe = LocalFailsafe.State,
            });
        }

        if (state != AgentState.Active || _sim.Session is not { } session || _sim.Frame is not { } frame)
        {
            return;
        }

        LocalFailsafe.Tick(new LocalFailsafeTick(
            now - _lastServerContact,
            _rank ?? (IsExecutor ? 0 : 1),
            LocalFailsafeArmed(session, frame),
            FlagNormalizer.IsCaution(frame.SessionFlags),
            LocalFacts.Of(FlagNormalizer.Normalize(frame.SessionFlags, frame.SessionState),
                frame.Car(session.PaceCarIdx) is { } pace && pace.Surface == SimValues.SurfaceOnTrack)));

        foreach (var e in _detector.Observe(session, frame))
        {
            var message = Events.Add(_options.AgentId, ServerNow, e);
            _logger.LogInformation("Event {Seq} {Type}", message.Seq, message.EventType);
            _server.Send(message);
            if (e.Data is SessionChangeData s)
            {
                _identity = new SessionIdentity(s.LeagueId > 0 ? s.LeagueId : _identity?.LeagueId ?? 0, s.SeasonId, s.SessionId, s.SubSessionId, s.Num, s.Type);
                // The server binds by identity: tell it (the answer may move the agent to another session).
                _ = SendHelloAsync(CancellationToken.None);
            }
        }

        // iRacing's results, whenever the session info changed them (spec-feedback 42).
        if (session.UpdateCount != _resultsUpdate)
        {
            _resultsUpdate = session.UpdateCount;
            foreach (var results in _results.Observe(session))
            {
                var message = Events.Add(_options.AgentId, ServerNow, new DetectedEvent(EventType.SessionResults, results));
                _logger.LogInformation("Event {Seq} session_results session {Session}, {Laps} laps{Official}", message.Seq, results.SessionNum,
                    results.LapsComplete, results.Official ? ", official" : "");
                _server.Send(message);
            }
        }

        _crossingsSession = session;
        if (now >= _nextCrossings)
        {
            _nextCrossings = now + _options.CrossingsInterval;
            SendCrossings(frame.SessionNum);
        }

        if (now >= _nextSnapshot)
        {
            _nextSnapshot = now + (IsExecutor ? _options.SnapshotInterval : _options.StandbySnapshotInterval);
            var snapshot = SnapshotBuilder.Build(_options.AgentId, ++_snapshotSeq, _clock.UtcNow, session, frame);
            if (_identity is { LeagueId: > 0 } id && snapshot.Session.LeagueId == 0)
            {
                // The operator selected the league (spike 11 fallback); report it.
                snapshot = snapshot with { Session = snapshot.Session with { LeagueId = id.LeagueId } };
            }

            _server.Send(snapshot);
        }
    }

    /// <summary>
    /// Armed as the failsafe rules say (spec: Arming and session lifecycle): a race, or practice or qualifying where
    /// the league arms them, until the checkered flag. Active in it, which is checked before.
    /// </summary>
    private bool LocalFailsafeArmed(SimSession session, SimFrame frame)
    {
        if (frame.SessionState >= SimValues.StateCheckered || (frame.SessionFlags & SimValues.FlagCheckered) != 0)
        {
            return false;
        }

        var copy = LocalFailsafeCopies.Held;
        return session.SessionType switch
        {
            "Race" => true,
            var t when t.Contains("Practice", StringComparison.OrdinalIgnoreCase) => copy?.ArmPractice == true,
            var t when t.Contains("Qualify", StringComparison.OrdinalIgnoreCase) => copy?.ArmQualify == true,
            _ => false,
        };
    }

    /// <summary>Every telemetry frame, on the sim's thread: the sector crossings since the previous one, while active.</summary>
    private void OnFrameArrived(SimFrame frame)
    {
        if (States.State != AgentState.Active || _crossingsSession is not { } session)
        {
            return;
        }

        lock (_crossingsGate)
        {
            _pendingCrossings.AddRange(_crossings.Observe(session, frame));
        }
    }

    /// <summary>The crossings collected since the last send, as sector_crossings events (buffered like every event).</summary>
    private void SendCrossings(int sessionNum)
    {
        List<SectorCrossingData> due;
        lock (_crossingsGate)
        {
            if (_pendingCrossings.Count == 0)
            {
                return;
            }

            due = [.. _pendingCrossings];
            _pendingCrossings.Clear();
        }

        foreach (var chunk in due.Chunk(MaxCrossingsPerEvent))
        {
            var message = Events.Add(_options.AgentId, ServerNow,
                new DetectedEvent(EventType.SectorCrossings, new SectorCrossingsData(Math.Max(0, sessionNum), chunk)));
            _logger.LogDebug("Event {Seq} sector_crossings ({Count})", message.Seq, chunk.Length);
            _server.Send(message);
        }
    }

    /// <summary>A local_failsafe event: buffered like every event, so it reaches the server after the outage.</summary>
    private void ReportLocalFailsafe(LocalFailsafeEventData data)
    {
        var message = Events.Add(_options.AgentId, ServerNow, new DetectedEvent(EventType.LocalFailsafe, data));
        _logger.LogInformation("Event {Seq} local_failsafe {Action}", message.Seq, data.Action);
        _server.Send(message);
    }

    void IStatusSink.Report(CommandStatusMessage status)
    {
        if (States.State != AgentState.Sleeping)
        {
            _server.Send(status);
        }
    }

    private void OnCommand(CommandMessage command)
    {
        switch (States.State)
        {
            case AgentState.Active when IsExecutor || CommandRenderer.ViewTypes.Contains(command.Type):
                // View commands (replays) run on any active agent: they change only this PC's view (spec-feedback 41).
                Executor.Submit(command);
                break;
            case AgentState.Active:
                Reject(command, "this agent is not the executor");
                break;
            case AgentState.Ready:
                Reject(command, "agent is ready but not active in a session");
                break;
            default:
                // Sleeping (and self-test) means silent.
                _logger.LogInformation("Command {Id} ignored in state {State}", command.Id, States.State);
                break;
        }
    }

    private void Reject(CommandMessage command, string reason) =>
        _server.Send(new CommandStatusMessage
        {
            CommandId = command.Id,
            Step = CommandStep.Result,
            Outcome = CommandOutcome.Failed,
            At = ServerNow,
            Detail = reason,
        });

    private async Task SendHelloAsync(CancellationToken ct)
    {
        var s = _sim.Session;
        try
        {
            var view = await _server.HelloAsync(new HelloMessage
            {
                AgentId = _options.AgentId,
                State = States.State,
                AgentVersion = _options.AgentVersion,
                Session = _identity?.ToContract(),
                Operator = s is null ? null : new OperatorInfo(s.OperatorCustId > 0 ? s.OperatorCustId : null, s.OperatorName),
                Channels = Channels,
            }, ct).ConfigureAwait(false);
            if (States.State is AgentState.Ready or AgentState.Active)
            {
                ApplyView(view);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The heartbeat results bring the view once the server answers again.
            _logger.LogWarning("Hello failed: {Message}", e.Message);
        }
    }

    /// <summary>After an outage: the events the server has not acknowledged, oldest first.</summary>
    private void Resync()
    {
        if (States.State is AgentState.Ready or AgentState.Active)
        {
            var missed = Events.After(Events.LastAcked);
            if (Events.Dropped > _reportedDropped)
            {
                // The buffer was full: the outage report covers the gap, the operator's log says how big it was.
                _logger.LogWarning("{Count} events were lost during the outage: the buffer holds {Capacity}", Events.Dropped - _reportedDropped, EventBufferCapacity);
                _reportedDropped = Events.Dropped;
            }

            // The outage as this agent saw it, on the server's clock: the server audits the agent's view of it.
            var outage = _server.LastOutage is { } o ? new OutageWindow(o.From + _clockOffset.Offset, o.To + _clockOffset.Offset) : null;
            _logger.LogInformation("Resync: {Count} events after {Seq}, outage {Outage}", missed.Count, Events.LastAcked, outage);
            _server.Send(new ResyncMessage { LastAckedSeq = Events.LastAcked, Outage = outage, Events = missed });
        }

        // A hello lost in the outage would leave the server without the session identity.
        if (States.State == AgentState.Ready)
        {
            _ = SendHelloAsync(CancellationToken.None);
        }
    }
}
