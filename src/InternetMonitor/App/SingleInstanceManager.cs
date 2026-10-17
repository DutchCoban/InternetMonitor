namespace InternetMonitor.App;

/// <summary>
/// Holds a named mutex for the lifetime of the process so a second launch exits instead of
/// starting a second tray icon and a second set of probe loops.
///
/// The names are explicitly in the session-local namespace ("Local\") rather than machine-wide:
/// two different users logged into the same machine should each get their own monitor, since each
/// has their own settings, their own history and their own tray - and nothing in another session
/// can reach these objects at all.
/// </summary>
public sealed class SingleInstanceManager : IDisposable
{
    private const string MutexName = @"Local\InternetMonitor.SingleInstance.Mutex";

    /// <summary>Set by a second launch to ask the running instance to show its window.</summary>
    private const string ShowEventName = @"Local\InternetMonitor.SingleInstance.Show";

    private readonly Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;

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

    /// <summary>
    /// From a second launch: asks the instance already running to bring its window up. Does
    /// nothing when that instance is not listening (yet), which only leaves it in the tray.
    /// </summary>
    public static void RequestShow()
    {
        try
        {
            using EventWaitHandle show = EventWaitHandle.OpenExisting(ShowEventName);
            show.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            // Nothing is listening; the running instance stays as it is.
        }
    }

    /// <summary>Calls <paramref name="onShow"/>, on a thread-pool thread, whenever a second launch asks for the window.</summary>
    public void ListenForShow(Action onShow)
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => onShow(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _showWait?.Unregister(null);
        _showEvent?.Dispose();

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
