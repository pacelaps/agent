using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;
using PaceLaps.Agent.Core.Time;
using PaceLaps.Agent.Core.TypedChat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.Commands;

/// <summary>
/// A command this agent finished, for the operator's list of recent commands: what was typed (or what the command
/// does, for one that types nothing), how it ended and how long it took from queueing. Local only, never sent.
/// </summary>
public sealed record FinishedCommand(string Id, string Type, string Text, CommandOutcome Outcome, string? Detail, DateTimeOffset At, TimeSpan Took, bool Local);

/// <summary>Where command status steps go (the server link in the host).</summary>
public interface IStatusSink
{
    void Report(CommandStatusMessage status);
}

public sealed record ExecutorOptions
{
    /// <summary>How many command ids are remembered to stop a command from running twice.</summary>
    public int RememberedIds { get; init; } = 500;

    /// <summary>Poll interval while waiting for a command's effect in telemetry.</summary>
    public TimeSpan VerifyPollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// After a posted line (spec-feedback 37), how long the queue waits for a quickly verifiable effect before the
    /// line is typed once more the focus way. Spike 16 saw the effects 0.30 to 0.42 s after the line.
    /// </summary>
    public TimeSpan PostedCheck { get; init; } = TimeSpan.FromSeconds(1.5);
}

/// <summary>
/// Executes server commands (spec: Command channels, Executor model, Action status steps):
/// received, then the epoch and duplicate checks and rendering, then one queue in arrival order that waits for
/// each command's execute-at time, picks the channel, executes, and reports executing and the result. A result
/// is verified when telemetry shows the effect, sent when the command went out but no effect is visible or
/// verifiable, failed with a reason otherwise. Verification runs beside the queue, so a slow effect never delays
/// the next command. One exception (spec-feedback 37): a typed line sent the posted way cannot be read back, so for a
/// quickly verifiable command the queue waits up to <see cref="ExecutorOptions.PostedCheck"/> for the effect, and
/// types the line once more the focus way (with the chat box readback) if it is not seen. Doing this inside the queue
/// keeps the order: a repeat never overtakes a later command (a !black after a later !clear).
/// </summary>
public sealed class CommandExecutor
{
    private readonly CommandRenderer _renderer;
    private readonly TypedChatSender _typed;
    private readonly IMacroChannel? _macro;
    private readonly MacroTable _macros;
    private readonly IReplayChannel? _replay;
    private readonly ISimData _sim;
    private readonly IClock _clock;
    private readonly IStatusSink _sink;
    private readonly Func<ChannelSet> _readiness;
    private readonly Func<TimeSpan> _serverOffset;

    /// <summary>Now on the server's clock: every time the agent reports is (spec: Connection loss and resync).</summary>
    private DateTimeOffset ServerNow => _clock.UtcNow + _serverOffset();
    private readonly ExecutorOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<(CommandMessage Command, RenderedCommand Rendered)> _queue = Channel.CreateUnbounded<(CommandMessage, RenderedCommand)>();
    private readonly Dictionary<string, CommandStatusMessage?> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private readonly List<Task> _verifications = [];
    private readonly object _gate = new();

    /// <param name="readiness">Current per-channel readiness; only ready channels are used.</param>
    /// <param name="serverOffset">Server clock minus local clock, for execute-at times.</param>
    public CommandExecutor(CommandRenderer renderer, TypedChatSender typed, IMacroChannel? macro, MacroTable? macros,
        IReplayChannel? replay, ISimData sim, IClock clock, IStatusSink sink, Func<ChannelSet> readiness, Func<TimeSpan> serverOffset,
        ExecutorOptions? options = null, ILogger<CommandExecutor>? logger = null)
    {
        _renderer = renderer;
        _typed = typed;
        _macro = macro;
        _macros = macros ?? MacroTable.Empty;
        _replay = replay;
        _sim = sim;
        _clock = clock;
        _sink = sink;
        _readiness = readiness;
        _serverOffset = serverOffset;
        _options = options ?? new ExecutorOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Typed failures of the channel itself in a row after which the channel counts as failed (spec: "if the executor
    /// cannot type, the server promotes the next active agent"); decided 2026-10-27.
    /// </summary>
    public const int TypedFailuresBeforeChannelFails = 2;

    /// <summary>Failures of the typed channel itself, not of one command: iRacing cannot be reached or typed into.</summary>
    private static readonly HashSet<TypedChatFailure> ChannelFailures =
    [
        TypedChatFailure.SimNotFound, TypedChatFailure.FocusFailed, TypedChatFailure.FocusLost, TypedChatFailure.KeyboardRejected,
        TypedChatFailure.LineNotSent, TypedChatFailure.Error, TypedChatFailure.PostRejected,
    ];

    private int _typedChannelFailures;
    private int _localCount;

    /// <summary>The local failsafe's own commands: their results stay here instead of going to the server.</summary>
    private readonly Dictionary<string, TaskCompletionSource<(CommandOutcome Outcome, string? Detail)>> _local = new(StringComparer.Ordinal);

    /// <summary>The typed channel failed <see cref="TypedFailuresBeforeChannelFails"/> times in a row; the reason of the last.</summary>
    public event Action<string>? TypedChannelFailed;

    /// <summary>A command got its result (the server's and the local failsafe's): for the operator's recent commands.</summary>
    public event Action<FinishedCommand>? Finished;

    /// <summary>Queued commands' text and start, until their result (<see cref="Finished"/>).</summary>
    private readonly Dictionary<string, (string Type, string Text, TimeSpan Queued, bool Local)> _pending = new(StringComparer.Ordinal);

    /// <summary>The executor epoch the server assigned. Commands with an older epoch are rejected.</summary>
    public long Epoch { get; set; }

    /// <summary>Milliseconds from receiving the last command to it being sent, for the heartbeat.</summary>
    public int? LastLatencyMs { get; private set; }

    /// <summary>Checks and queues a command. Reports received, and a failed result if it is rejected.</summary>
    public void Submit(CommandMessage command)
    {
        Report(command.Id, CommandStep.ReceivedByExecutor);

        lock (_gate)
        {
            if (_seen.TryGetValue(command.Id, out var earlier))
            {
                // Never run a command twice (a non-idempotent one must not repeat after a reconnect).
                _logger.LogWarning("Command {Id} received again; not executed again", command.Id);
                if (earlier is not null)
                {
                    _sink.Report(earlier with { At = ServerNow, Detail = Join(earlier.Detail, "duplicate command id, not executed again") });
                }

                return;
            }

            Remember(command.Id);
        }

        // View commands change only this PC's view (spec-feedback 41): no executor epoch.
        if (command.Epoch < Epoch && !CommandRenderer.ViewTypes.Contains(command.Type))
        {
            Result(command.Id, CommandOutcome.Failed, $"stale epoch {command.Epoch}, current is {Epoch}");
            return;
        }

        var rendered = _renderer.Render(command.Type, command.ParamsOrEmpty);
        if (!rendered.Ok)
        {
            Result(command.Id, CommandOutcome.Failed, "rejected: " + rendered.Error);
            return;
        }

        _logger.LogInformation("Command {Id} {Type} queued: {Text}", command.Id, command.Type, rendered.Command!.ChatText);
        Pending(command.Id, rendered.Command!, local: false);
        _queue.Writer.TryWrite((command, rendered.Command!));
    }

    /// <summary>
    /// A command of the local failsafe (no server, no epoch): rendered and queued like the server's, through the same
    /// channels, and its result returned instead of reported. It waits for verification where the command has one.
    /// </summary>
    public Task<(CommandOutcome Outcome, string? Detail)> ExecuteLocalAsync(string type, JsonElement parameters)
    {
        var id = $"local_{Interlocked.Increment(ref _localCount)}";
        var done = new TaskCompletionSource<(CommandOutcome, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = _renderer.Render(type, parameters);
        if (!rendered.Ok)
        {
            done.SetResult((CommandOutcome.Failed, "rejected: " + rendered.Error));
            return done.Task;
        }

        lock (_gate)
        {
            _local[id] = done;
        }

        var command = new CommandMessage
        {
            Id = id, Epoch = Epoch, Type = type, Params = parameters, Idempotent = CommandCatalogue.Idempotent(type),
            IssuedAt = _clock.UtcNow, IssuedBy = "local-failsafe",
        };
        _logger.LogInformation("Local failsafe command {Id} {Type} queued: {Text}", id, type, rendered.Command!.ChatText);
        Pending(id, rendered.Command!, local: true);
        _queue.Writer.TryWrite((command, rendered.Command!));
        return done.Task;
    }

    /// <summary>Runs the queue until cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_queue.Reader.TryRead(out var item))
                {
                    await ExecuteAsync(item.Command, item.Rendered, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down; queued commands are not executed.
        }
    }

    /// <summary>Executes everything queued so far (tests, and the host's single-step mode).</summary>
    public async Task<int> ProcessQueuedAsync(CancellationToken ct = default)
    {
        var n = 0;
        while (_queue.Reader.TryRead(out var item))
        {
            await ExecuteAsync(item.Command, item.Rendered, ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>Drops queued commands without executing or reporting them (the agent went to sleep).</summary>
    public int DropQueued()
    {
        var n = 0;
        while (_queue.Reader.TryRead(out _))
        {
            n++;
        }

        if (n > 0)
        {
            _logger.LogWarning("{Count} queued command(s) dropped", n);
        }

        return n;
    }

    /// <summary>Waits until every running verification has reported its result.</summary>
    public async Task WaitForVerificationsAsync()
    {
        Task[] running;
        lock (_gate)
        {
            running = [.. _verifications];
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task ExecuteAsync(CommandMessage command, RenderedCommand rendered, CancellationToken ct)
    {
        var receivedAt = _clock.Elapsed;
        var late = await WaitForExecuteAtAsync(command, ct).ConfigureAwait(false);

        var before = _sim.Frame;
        if (before is not null && CommandVerifier.AlreadyInEffect(rendered, before) is { } already)
        {
            Result(command.Id, CommandOutcome.Verified, Join(already, late));
            return;
        }

        var channels = _readiness();
        string channel;
        string? failure = null;
        TimeSpan sendTime = TimeSpan.Zero;
        switch (rendered.Channels)
        {
            case ChannelOptions.Sdk:
                channel = "sdk";
                Report(command.Id, CommandStep.Executing, Join("channel sdk", late));
                if (channels.Sdk != ChannelReadiness.Ready || _replay is null)
                {
                    failure = $"sdk channel is {channels.Sdk.ToString().ToLowerInvariant()}";
                    break;
                }

                if (rendered.Replay is { } target)
                {
                    _replay.Jump(target.SessionNum, target.SessionTimeSec, target.Car);
                }
                else
                {
                    _replay.Live();
                }

                break;

            default:
                var slot = rendered.Channels == ChannelOptions.MacroOrTyped && channels.Macro == ChannelReadiness.Ready && _macro is not null
                    ? _macros.SlotFor(rendered.ChatText!)
                    : null;
                if (slot is { } s)
                {
                    channel = $"macro slot {s}";
                    Report(command.Id, CommandStep.Executing, Join($"channel {channel}", late));
                    _macro!.Trigger(s);
                    break;
                }

                channel = "typed";
                Report(command.Id, CommandStep.Executing, Join("channel typed", late));
                if (channels.Typed != ChannelReadiness.Ready)
                {
                    failure = $"typed channel is {channels.Typed.ToString().ToLowerInvariant()}";
                    break;
                }

                var typed = await _typed.SendAsync(rendered.ChatText!, ct).ConfigureAwait(false);
                sendTime = typed.TotalTime;
                if (typed is { Success: true, Way: TypedChatWay.Posted } && before is not null
                    && CommandVerifier.For(rendered, _sim.Session, before) is { } quick && quick.Timeout <= CommandVerifier.QuickTimeout
                    && !quick.Effect(before) && !await SeenWithinAsync(quick, _options.PostedCheck, ct).ConfigureAwait(false))
                {
                    // Not seen: the posted line was probably lost (it cannot be read back). Typed once more the focus
                    // way, which reads the chat box; the normal check beside the queue follows.
                    var waited = string.Create(CultureInfo.InvariantCulture, $"{_options.PostedCheck.TotalSeconds:0.#} s");
                    _logger.LogWarning("Command {Id}: {Effect} not seen {Waited} after the posted line; typing it again with the focus switch",
                        command.Id, quick.Description, waited);
                    typed = await _typed.SendAsync(rendered.ChatText!, TypedChatWay.Focus, ct).ConfigureAwait(false);
                    sendTime += typed.TotalTime;
                    late = Join(late, $"repeated with the focus switch: {quick.Description} not seen {waited} after the line");
                }

                if (!typed.Success)
                {
                    failure = $"{typed.Failure}: {typed.Detail}";
                    // Text the layout cannot type, a cancel or the operator's own text in the box: this command, not the channel.
                    if (ChannelFailures.Contains(typed.Failure) && ++_typedChannelFailures == TypedFailuresBeforeChannelFails)
                    {
                        _logger.LogWarning("The typed channel failed {Count} times in a row ({Failure}): reported as failed", _typedChannelFailures, failure);
                        TypedChannelFailed?.Invoke(failure);
                    }
                }
                else
                {
                    _typedChannelFailures = 0;
                    if (!typed.FocusRestored)
                    {
                        _logger.LogWarning("Command {Id}: focus was not restored", command.Id);
                    }

                    // For example "a second Enter sent the line": goes into the result detail.
                    late = Join(late, typed.Note);
                }

                break;
        }

        if (failure is not null)
        {
            Result(command.Id, CommandOutcome.Failed, failure);
            return;
        }

        LastLatencyMs = (int)Math.Round((_clock.Elapsed - receivedAt).TotalMilliseconds);
        _logger.LogInformation("Command {Id} sent via {Channel} in {Ms:F0} ms", command.Id, channel, sendTime.TotalMilliseconds);

        var plan = before is null ? null : CommandVerifier.For(rendered, _sim.Session, before);
        if (plan is null)
        {
            Result(command.Id, CommandOutcome.Sent, Join($"via {channel}; not verifiable in telemetry", late));
            return;
        }

        // The effect was there before the command (acceptance run: a second black flag on #12), so telemetry
        // cannot show that this command did anything.
        if (plan.Effect(before!))
        {
            Result(command.Id, CommandOutcome.Sent, Join($"via {channel}; {plan.Description} was already showing before the command", late));
            return;
        }

        var verification = VerifyAsync(command.Id, plan, channel, late);
        lock (_gate)
        {
            _verifications.RemoveAll(t => t.IsCompleted);
            _verifications.Add(verification);
        }
    }

    private async Task VerifyAsync(string id, VerificationPlan plan, string channel, string? late)
    {
        var deadline = _clock.Elapsed + plan.Timeout;
        while (true)
        {
            if (_sim.Frame is { } f && plan.Effect(f))
            {
                Result(id, CommandOutcome.Verified, Join($"via {channel}; {plan.Description}", late));
                return;
            }

            if (_clock.Elapsed >= deadline)
            {
                Result(id, CommandOutcome.Sent, Join(string.Create(CultureInfo.InvariantCulture, $"via {channel}; {plan.Description} not seen within {plan.Timeout.TotalSeconds:F0} s"), late));
                return;
            }

            await _clock.Delay(_options.VerifyPollInterval).ConfigureAwait(false);
        }
    }

    /// <summary>True as soon as telemetry shows the effect; false after <paramref name="within"/>, with one last look.</summary>
    private async Task<bool> SeenWithinAsync(VerificationPlan plan, TimeSpan within, CancellationToken ct)
    {
        var deadline = _clock.Elapsed + within;
        while (true)
        {
            if (_sim.Frame is { } f && plan.Effect(f))
            {
                return true;
            }

            if (_clock.Elapsed >= deadline)
            {
                return false;
            }

            await _clock.Delay(_options.VerifyPollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Waits until the execute-at time on the server clock. Returns a note if the command is late.</summary>
    private async Task<string?> WaitForExecuteAtAsync(CommandMessage command, CancellationToken ct)
    {
        if (command.ExecuteAt is not { } executeAt)
        {
            return null;
        }

        var localTarget = executeAt - _serverOffset();
        var wait = localTarget - _clock.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await _clock.Delay(wait, ct).ConfigureAwait(false);
            return null;
        }

        return wait < -TimeSpan.FromMilliseconds(250) ? string.Create(CultureInfo.InvariantCulture, $"late by {-wait.TotalSeconds:F1} s") : null;
    }

    private void Report(string id, CommandStep step, string? detail = null)
    {
        lock (_gate)
        {
            if (_local.ContainsKey(id))
            {
                return;
            }
        }

        _sink.Report(new CommandStatusMessage { CommandId = id, Step = step, At = ServerNow, Detail = detail });
    }

    private void Result(string id, CommandOutcome outcome, string? detail)
    {
        TaskCompletionSource<(CommandOutcome, string?)>? local;
        (string Type, string Text, TimeSpan Queued, bool Local) pending;
        bool wasPending;
        lock (_gate)
        {
            wasPending = _pending.Remove(id, out pending);
        }

        if (wasPending)
        {
            Finished?.Invoke(new FinishedCommand(id, pending.Type, pending.Text, outcome, detail, _clock.UtcNow, _clock.Elapsed - pending.Queued, pending.Local));
        }

        lock (_gate)
        {
            if (_local.Remove(id, out local))
            {
                _logger.LogInformation("Local failsafe command {Id}: {Outcome} {Detail}", id, outcome, detail);
            }
        }

        if (local is not null)
        {
            local.SetResult((outcome, detail));
            return;
        }

        var status = new CommandStatusMessage { CommandId = id, Step = CommandStep.Result, Outcome = outcome, At = ServerNow, Detail = detail };
        lock (_gate)
        {
            if (_seen.ContainsKey(id))
            {
                _seen[id] = status;
            }
        }

        if (outcome == CommandOutcome.Failed)
        {
            _logger.LogWarning("Command {Id} failed: {Detail}", id, detail);
        }
        else
        {
            _logger.LogInformation("Command {Id} {Outcome}: {Detail}", id, outcome, detail);
        }

        _sink.Report(status);
    }

    private void Pending(string id, RenderedCommand rendered, bool local)
    {
        var text = rendered.ChatText ?? rendered.Type switch
        {
            "replay_jump" => rendered.Car is { } car ? $"Replay of #{car}" : "Replay",
            "replay_live" => "Replay back to live",
            var type => type,
        };
        lock (_gate)
        {
            _pending[id] = (rendered.Type, text, _clock.Elapsed, local);
        }
    }

    private void Remember(string id)
    {
        _seen[id] = null;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > _options.RememberedIds)
        {
            _seen.Remove(_seenOrder.Dequeue());
        }
    }

    private static string? Join(string? a, string? b) =>
        a is null ? b : b is null ? a : $"{a}; {b}";
}
