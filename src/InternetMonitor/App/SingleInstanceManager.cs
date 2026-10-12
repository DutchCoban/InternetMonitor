namespace InternetMonitor.App;

/// <summary>
/// Holds a named mutex for the lifetime of the process so a second launch exits instead of
/// starting a second tray icon and a second set of probe loops.
///
/// The mutex name is global to the user session rather than machine-wide: two different users
/// logged into the same machine should each get their own monitor, since each has their own
/// settings, their own history and their own tray.
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = "InternetMonitor.SingleInstance.Mutex";

    private readonly Mutex? _mutex;

    private SingleInstanceManager(Mutex? mutex) => _mutex = mutex;

    /// <summary>
    /// Returns null when another instance already holds the mutex. An abandoned mutex (the
    /// previous instance crashed without releasing it) still counts as acquired - that is exactly
    /// the case where this process should take over.
    /// </summary>
    public static SingleInstanceManager? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                return new SingleInstanceManager(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstanceManager(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread (shutdown from a different context) - the handle close below
            // releases it anyway.
        }

        _mutex.Dispose();
    }
}
