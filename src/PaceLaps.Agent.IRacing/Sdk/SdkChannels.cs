using System.Globalization;
using PaceLaps.Agent.Core.Commands;

namespace PaceLaps.Agent.IRacing.Sdk;

/// <summary>Chat macros through the SDK broadcast message (no focus needed; spike 1).</summary>
public sealed class SdkMacroChannel(SimConnection sim) : IMacroChannel
{
    public void Trigger(int slot) => sim.TriggerMacro(slot);
}

/// <summary>Replay jumps through the SDK broadcast message (spike 6); the camera keeps its group.</summary>
public sealed class SdkReplayChannel(SimConnection sim) : IReplayChannel
{
    public void Jump(int sessionNum, double sessionTimeSec, string? car = null)
    {
        sim.ReplayJump(sessionNum, sessionTimeSec);
        if (car is not null)
        {
            var group = sim.Latest?.Get("CamGroupNumber") is { } g ? Convert.ToInt32(g, CultureInfo.InvariantCulture) : 0;
            sim.CameraOnCar(car, group);
        }
    }

    public void Live() => sim.ReplayReturnToLive();
}
