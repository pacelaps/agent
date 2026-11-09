using PaceLaps.Agent.Core.Commands;

namespace PaceLaps.Agent.Hosting;

/// <summary>
/// The commands this agent finished lately, newest first, for the app's "Last commands" (the WPF plan, step 2). It
/// lives as long as the program, so the list survives Sleep and Activate. Thread-safe: the executor reports from its
/// queue, the app reads on its own thread.
/// </summary>
public sealed class RecentCommands(int capacity = RecentCommands.DefaultCapacity)
{
    public const int DefaultCapacity = 20;

    private readonly LinkedList<FinishedCommand> _items = new();
    private readonly object _gate = new();

    public void Add(FinishedCommand command)
    {
        lock (_gate)
        {
            _items.AddFirst(command);
            while (_items.Count > capacity)
            {
                _items.RemoveLast();
            }
        }
    }

    public IReadOnlyList<FinishedCommand> Items()
    {
        lock (_gate)
        {
            return [.. _items];
        }
    }
}
