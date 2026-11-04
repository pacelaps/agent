namespace PaceLaps.Agent.Core.Commands;

/// <summary>The SDK chat macro channel (no focus needed; spike 1).</summary>
public interface IMacroChannel
{
    /// <summary>Runs chat macro <paramref name="slot"/> (1 to 15, as numbered in the sim's options).</summary>
    void Trigger(int slot);
}

/// <summary>The SDK replay messages (spike 6): this PC's view only; telemetry stays live meanwhile.</summary>
public interface IReplayChannel
{
    /// <summary>Moves the local replay to a session time; with <paramref name="car"/> the camera follows that car.</summary>
    void Jump(int sessionNum, double sessionTimeSec, string? car = null);

    /// <summary>Back to the live session.</summary>
    void Live();
}

/// <summary>
/// The operator's macro slots that the agent may use: slot and its exact text. The sim slot must contain the chat
/// text followed by "$", so it is sent at once. Only slots listed here are ever triggered; the operator's own
/// racing macros stay untouched.
/// </summary>
public sealed class MacroTable
{
    private readonly Dictionary<string, int> _slotByText = new(StringComparer.Ordinal);

    public MacroTable(IEnumerable<(int Slot, string Text)> assignments)
    {
        foreach (var (slot, raw) in assignments)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, 15);
            var text = raw.EndsWith('$') ? raw[..^1] : raw;
            if (_slotByText.Values.Contains(slot))
            {
                throw new ArgumentException($"macro slot {slot} is assigned twice");
            }

            _slotByText[text] = slot;
        }
    }

    public static MacroTable Empty { get; } = new([]);

    public int Count => _slotByText.Count;

    public IReadOnlyDictionary<string, int> Assignments => _slotByText;

    /// <summary>The slot whose text is exactly this chat line, if any.</summary>
    public int? SlotFor(string chatText) => _slotByText.TryGetValue(chatText, out var slot) ? slot : null;
}
