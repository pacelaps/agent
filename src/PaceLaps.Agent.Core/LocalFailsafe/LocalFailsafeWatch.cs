using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Config;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PaceLaps.Agent.Core.LocalFailsafe;

/// <summary>What the host knows on each tick, for the watch.</summary>
/// <param name="SinceServerContact">Since the server last answered.</param>
/// <param name="Rank">The agent's place in the stagger (the executor is 0).</param>
/// <param name="Armed">Active in a session the failsafe watches (a race, or practice or qualifying if the league arms them), before the checkered flag.</param>
/// <param name="Caution">A full-course caution is out (another agent, or the admins, acted).</param>
public sealed record LocalFailsafeTick(TimeSpan SinceServerContact, int Rank, bool Armed, bool Caution, LocalFacts Facts);

/// <summary>
/// The local failsafe's watchdog and driver (spec: Local failsafe, opt-in; Failure modes). It starts the copied
/// procedure when the operator turned it on, a copy is held, the session is armed, no caution is out, and the server has
/// been gone for the operator's delay plus <see cref="StaggerPerRank"/> per rank (agents act one after another; each
/// checks the caution first). One run per outage: after it ends it does not start again until the server has been back
/// (otherwise it would throw a new caution after the green flag). It never releases itself: a steward releases it
/// through the server. Commands go through the agent's channels; a failed idempotent one is tried once more after
/// <see cref="RetryAfter"/>, then the run goes on. Start, commands, end and release become local_failsafe events.
/// </summary>
public sealed class LocalFailsafeWatch
{
    public static readonly TimeSpan StaggerPerRank = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(2);

    private readonly LocalFailsafeSettings _settings;
    private readonly LocalFailsafeCopies _copies;
    private readonly Func<string, System.Text.Json.JsonElement, Task<(CommandOutcome Outcome, string? Detail)>> _execute;
    private readonly Action<LocalFailsafeEventData> _report;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private LocalFailsafeRun? _run;
    private LocalFailsafeCopy? _running;
    private LocalCommand? _command;
    private Task<(CommandOutcome Outcome, string? Detail)>? _pending;
    private bool _retried;
    private TimeSpan? _retryAt;
    private bool _spent;
    private bool _toldCaution;

    /// <param name="execute">Sends a command through the agent's channels and returns its result.</param>
    /// <param name="report">Buffers a local_failsafe event (it reaches the server, also after the outage).</param>
    public LocalFailsafeWatch(LocalFailsafeSettings settings, LocalFailsafeCopies copies,
        Func<string, System.Text.Json.JsonElement, Task<(CommandOutcome Outcome, string? Detail)>> execute, Action<LocalFailsafeEventData> report,
        IClock clock, ILogger? logger = null)
    {
        _settings = settings;
        _copies = copies;
        _execute = execute;
        _report = report;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool Running => _run is not null;

    public DateTimeOffset? Since { get; private set; }

    /// <summary>The heartbeat's localFailsafe.</summary>
    public LocalFailsafeState State => new()
    {
        Enabled = _settings.Enabled,
        Version = _copies.Held?.Version,
        Hash = _copies.Held?.Hash,
        Running = Running,
        Since = Running ? Since : null,
    };

    /// <summary>The delay before this agent acts, by its rank.</summary>
    public TimeSpan DelayFor(int rank) => _settings.Delay + StaggerPerRank * Math.Max(0, rank);

    public void Tick(LocalFailsafeTick tick)
    {
        if (_run is not null)
        {
            Drive(tick);
            return;
        }

        if (!_settings.Enabled || _spent || !tick.Armed || _copies.Held is not { } copy || tick.SinceServerContact < DelayFor(tick.Rank))
        {
            return;
        }

        if (tick.Caution)
        {
            if (!_toldCaution)
            {
                _logger.LogWarning("Local failsafe: no server for {Seconds:F0} s, but a caution is already out: not acting", tick.SinceServerContact.TotalSeconds);
                _toldCaution = true;
            }

            return;
        }

        if (copy.Read() is not { } procedure)
        {
            _logger.LogError("Local failsafe: the copy of {Procedure} v{Version} cannot run", copy.Procedure, copy.Version);
            _spent = true;
            return;
        }

        _run = new LocalFailsafeRun(procedure);
        _running = copy;
        _spent = true;
        Since = _clock.UtcNow;
        _logger.LogWarning("Local failsafe: no server for {Seconds:F0} s (rank {Rank}): running {Procedure} v{Version}",
            tick.SinceServerContact.TotalSeconds, tick.Rank, copy.Procedure, copy.Version);
        Report(LocalFailsafeAction.Started, detail: $"no server contact for {tick.SinceServerContact.TotalSeconds:F0} s");
        Drive(tick);
    }

    /// <summary>The server answered again: after an outage whose run has ended, the next outage may run again.</summary>
    public void ServerBack()
    {
        _toldCaution = false;
        if (_run is null)
        {
            _spent = false;
        }
    }

    /// <summary>A steward released it (the heartbeat answer says so): it stops.</summary>
    public void Release()
    {
        if (_run is null)
        {
            return;
        }

        _run.Stop("released by a steward");
        _logger.LogWarning("Local failsafe released by a steward");
        Report(LocalFailsafeAction.Released);
        Finish();
    }

    /// <summary>The operator put the agent to sleep: silent, so it stops without events.</summary>
    public void Stop()
    {
        if (_run is not null)
        {
            _logger.LogWarning("Local failsafe stopped: the agent went to sleep");
            Finish();
        }
    }

    private void Drive(LocalFailsafeTick tick)
    {
        if (_pending is not null)
        {
            if (!_pending.IsCompleted)
            {
                return;
            }

            var (outcome, detail) = _pending.Result;
            _pending = null;
            if (outcome == CommandOutcome.Failed && !_retried && CommandCatalogue.Idempotent(_command!.Type))
            {
                _retried = true;
                _retryAt = _clock.Elapsed + RetryAfter;
                _logger.LogWarning("Local failsafe command {Type} failed ({Detail}); trying once more", _command.Type, detail);
                return;
            }

            Report(LocalFailsafeAction.Command, _command!.Type, outcome, detail);
            _command = null;
            _run!.CommandDone();
        }

        if (_retryAt is { } at)
        {
            if (_clock.Elapsed < at)
            {
                return;
            }

            _retryAt = null;
            _pending = _execute(_command!.Type, _command.Params);
            return;
        }

        if (_run!.Advance(_clock.Elapsed, tick.Facts) is { } next)
        {
            _command = next;
            _retried = false;
            _pending = _execute(next.Type, next.Params);
            return;
        }

        if (_run.Ended)
        {
            _logger.LogWarning("Local failsafe ended: {Reason}", _run.EndReason);
            Report(LocalFailsafeAction.Ended, detail: _run.EndReason);
            Finish();
        }
    }

    private void Finish()
    {
        _run = null;
        _command = null;
        _pending = null;
        _retryAt = null;
        Since = null;
    }

    private void Report(LocalFailsafeAction action, string? command = null, CommandOutcome? result = null, string? detail = null) =>
        _report(new LocalFailsafeEventData
        {
            Action = action,
            Procedure = _running!.Procedure,
            Version = _running.Version,
            Command = command,
            Result = result,
            Detail = detail is null ? null : detail.Length <= 200 ? detail : detail[..199] + "…",
        });
}
