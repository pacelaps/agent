using PaceLaps.Agent.Core.Contracts;

namespace PaceLaps.Agent.Core.States;

/// <summary>
/// The agent's states (spec: Agent specification &gt; States). Sleeping is the default; Activate starts the
/// self-test; a passed self-test makes the agent Ready; joining a session on the server makes it Active.
/// A failed self-test returns to Sleeping with the reason. The operator can go to Sleeping at any time. A lost
/// connection does not change the state (the agent buffers and resyncs), so there is no transition for it.
/// </summary>
public sealed class AgentStateMachine
{
    private readonly object _gate = new();

    public AgentState State { get; private set; } = AgentState.Sleeping;

    /// <summary>Why the last self-test failed, shown to the operator.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Raised after every transition: from, to, reason.</summary>
    public event Action<AgentState, AgentState, string?>? Changed;

    public bool Activate() => Move(AgentState.Sleeping, AgentState.SelfTest, "activated by the operator");

    public bool SelfTestPassed() => Move(AgentState.SelfTest, AgentState.Ready, "self-test passed");

    public bool SelfTestFailed(string reason)
    {
        lock (_gate)
        {
            if (State != AgentState.SelfTest)
            {
                return false;
            }

            LastFailure = reason;
        }

        return Move(AgentState.SelfTest, AgentState.Sleeping, "self-test failed: " + reason);
    }

    /// <summary>The server bound the agent to a new or existing session.</summary>
    public bool Joined() => Move(AgentState.Ready, AgentState.Active, "joined the session");

    /// <summary>The session ended or the server released the agent.</summary>
    public bool Left(string reason) => Move(AgentState.Active, AgentState.Ready, reason);

    /// <summary>The operator put the agent to sleep: all traffic stops.</summary>
    public bool Sleep()
    {
        AgentState from;
        lock (_gate)
        {
            from = State;
            if (from == AgentState.Sleeping)
            {
                return false;
            }

            State = AgentState.Sleeping;
        }

        Changed?.Invoke(from, AgentState.Sleeping, "put to sleep by the operator");
        return true;
    }

    private bool Move(AgentState from, AgentState to, string reason)
    {
        lock (_gate)
        {
            if (State != from)
            {
                return false;
            }

            State = to;
            if (to == AgentState.Ready)
            {
                LastFailure = null;
            }
        }

        Changed?.Invoke(from, to, reason);
        return true;
    }
}
