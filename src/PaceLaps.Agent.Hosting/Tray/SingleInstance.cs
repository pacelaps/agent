namespace PaceLaps.Agent.Hosting.Tray;

/// <summary>
/// One agent per Windows session: two would type every command twice. A second start tells the first one to show its
/// status window and ends.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = "PaceLaps.Agent";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _show;
    private readonly RegisteredWaitHandle _wait;

    private SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
        _wait = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => ShowRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Another start asked for the status window (on a pool thread).</summary>
    public event Action? ShowRequested;

    /// <summary>The instance, or null when one runs already (which is then asked to show itself).</summary>
    public static SingleInstance? TryAcquire(string name = DefaultName)
    {
        var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}", out var created);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.show");
        if (created)
        {
            return new SingleInstance(mutex, show);
        }

        show.Set();
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _wait.Unregister(null);
        _show.Dispose();
        _mutex.Dispose();
    }
}
