namespace PaceLaps.Agent.Core.SelfTest;

/// <summary>
/// Reads the sim's chat macro texts by slot (spike 10: app.ini, [Autochat Messages]). The sim writes them when it
/// closes, so a change made in a running sim shows only after it exits.
/// </summary>
public interface IMacroConfigSource
{
    /// <summary>Slot (1 to 15) to text as saved, or null if the configuration cannot be read.</summary>
    IReadOnlyDictionary<int, string>? ReadSlots();

    /// <summary>Where the configuration was read from, for the self-test report.</summary>
    string Location { get; }
}
