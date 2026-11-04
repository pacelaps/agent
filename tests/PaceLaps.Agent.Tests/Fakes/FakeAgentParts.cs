using PaceLaps.Agent.Core.Commands;
using PaceLaps.Agent.Core.Contracts;
using PaceLaps.Agent.Core.Sim;

namespace PaceLaps.Agent.Tests.Fakes;

/// <summary>A sim whose frame can depend on what was typed, so a command's effect can be simulated.</summary>
internal sealed class FakeSim : ISimData
{
    public bool IsConnected { get; set; } = true;
    public SimSession? Session { get; set; } = SimFixtures.Session();
    public Func<SimFrame?> FrameSource { get; set; } = () => SimFixtures.Frame();
    public SimFrame? Frame => FrameSource();
}

internal sealed class FakeStatusSink : IStatusSink
{
    public List<CommandStatusMessage> Statuses { get; } = [];

    public void Report(CommandStatusMessage status) => Statuses.Add(status);

    public IEnumerable<CommandStatusMessage> For(string id) => Statuses.Where(s => s.CommandId == id);

    public CommandStatusMessage ResultOf(string id) => Statuses.Last(s => s.CommandId == id && s.Step == CommandStep.Result);
}

internal sealed class FakeMacro : IMacroChannel
{
    public List<int> Triggered { get; } = [];

    public void Trigger(int slot) => Triggered.Add(slot);
}

internal sealed class FakeReplay : IReplayChannel
{
    public List<(int, double)> Jumps { get; } = [];

    /// <summary>The car the camera followed in each jump (null: the camera stayed).</summary>
    public List<string?> Cars { get; } = [];

    public int LiveCount { get; private set; }

    public void Jump(int sessionNum, double sessionTimeSec, string? car = null)
    {
        Jumps.Add((sessionNum, sessionTimeSec));
        Cars.Add(car);
    }

    public void Live() => LiveCount++;
}
