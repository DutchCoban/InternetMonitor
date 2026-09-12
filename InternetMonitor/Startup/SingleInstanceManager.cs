namespace InternetMonitor.Startup;

/// <summary>
/// Ensures only one instance of the app runs, via a named <see cref="Mutex"/>. The name is not
/// prefixed with "Global\", so this is scoped to the current Terminal Services session, not the
/// whole machine - a second instance under a different user session on the same machine is not
/// prevented. That's the intended behavior for a per-user tray app, not an oversight.
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private readonly Mutex _mutex;
    private bool _owned;

    public SingleInstanceManager(string name)
    {
        _mutex = new Mutex(initiallyOwned: false, name);
    }

    public bool TryAcquire()
    {
        try
        {
            _owned = _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance terminated without releasing the mutex; we still got it.
            _owned = true;
        }

        return _owned;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
