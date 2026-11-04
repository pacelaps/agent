using System.Text.Json;
using PaceLaps.Agent.Core.Contracts;

namespace PaceLaps.Agent.Core.LocalFailsafe;

/// <summary>A command the run wants sent: the driver executes it and reports back with <see cref="LocalFailsafeRun.CommandDone"/>.</summary>
public sealed record LocalCommand(string Type, JsonElement Params);

/// <summary>
/// One run of a local failsafe procedure: the interpreter, pure (time and telemetry come in). It mirrors the web
/// service's engine for the offline blocks: blocks run in order until one waits; a command waits for its result; a
/// wait_until or branch reads the facts; a goto or branch enters a phase; a phase without more blocks, or an end, ends
/// the run; more than <see cref="MaxBlocksPerStep"/> blocks without a wait end it as a loop. Unlike the server's
/// runs, nobody can decide on a failed command: the run goes on with the next block (decided 2026-10-27; the driver
/// retries idempotent commands once first).
/// </summary>
public sealed class LocalFailsafeRun
{
    public const int MaxBlocksPerStep = 200;

    private readonly LocalProcedure _procedure;
    private string _phase;
    private int _block;
    private TimeSpan? _waitUntil;
    private bool _waitingForCommand;

    public LocalFailsafeRun(LocalProcedure procedure)
    {
        _procedure = procedure;
        _phase = procedure.FirstPhase;
    }

    public bool Ended { get; private set; }

    /// <summary>Why the run ended (an end block, the phase ran out, a loop).</summary>
    public string? EndReason { get; private set; }

    public string Phase => _phase;

    /// <summary>
    /// Runs blocks until one waits. Returns the command to send, if a block wants one; then the run waits for
    /// <see cref="CommandDone"/>.
    /// </summary>
    public LocalCommand? Advance(TimeSpan now, LocalFacts facts)
    {
        if (Ended || _waitingForCommand)
        {
            return null;
        }

        for (var ran = 0; ; ran++)
        {
            if (ran >= MaxBlocksPerStep)
            {
                End($"more than {MaxBlocksPerStep} blocks without a wait (a loop?)");
                return null;
            }

            var blocks = _procedure.Phases[_phase];
            if (_block >= blocks.Count)
            {
                End("phase finished");
                return null;
            }

            switch (blocks[_block])
            {
                case LocalBlock.Command c:
                    _waitingForCommand = true;
                    return new LocalCommand(c.Type, c.Params);
                case LocalBlock.Wait w:
                    _waitUntil ??= now + w.Duration;
                    if (now < _waitUntil)
                    {
                        return null;
                    }

                    _waitUntil = null;
                    _block++;
                    break;
                case LocalBlock.WaitUntil u:
                    if (!u.Condition.Holds(facts))
                    {
                        return null;
                    }

                    _block++;
                    break;
                case LocalBlock.Branch b:
                    Enter(b.Condition.Holds(facts) ? b.Then : b.Else);
                    break;
                case LocalBlock.Goto g:
                    Enter(g.Phase);
                    break;
                case LocalBlock.End:
                    End("end");
                    return null;
            }
        }
    }

    /// <summary>The command the run waited for has its result (or the driver gave up on it): the run goes on.</summary>
    public void CommandDone()
    {
        if (_waitingForCommand)
        {
            _waitingForCommand = false;
            _block++;
        }
    }

    /// <summary>A steward released it (through the server): the run stops where it is.</summary>
    public void Stop(string reason) => End(reason);

    private void Enter(string phase)
    {
        _phase = phase;
        _block = 0;
        _waitUntil = null;
    }

    private void End(string reason)
    {
        Ended = true;
        EndReason = reason;
    }
}
