using InternetMonitor.Network;

namespace InternetMonitor.UI;

/// <summary>
/// Owns the outage popup's lifecycle rules, kept separate from the Form itself so the tricky
/// timing/dismissal logic can be reasoned about independently of WinForms control quirks.
///
/// Rules implemented:
///  - Shown exactly once per outage episode (on SuspectedOutage/Unknown -&gt; Outage).
///  - Stays visible at least <see cref="MinimumVisibleSeconds"/> seconds from first shown,
///    even if connectivity recovers sooner.
///  - A manual close is respected for the rest of that episode - no re-showing, even if the
///    outage continues for a long time. Only a fresh Outage entry (after returning to
///    Connected) can show a new popup.
///  - On genuine recovery (and only if the user hasn't already manually closed it), the popup
///    auto-closes once the minimum visible time has elapsed, and a recovered notification is
///    requested.
///
/// Must only be driven from the UI thread.
/// </summary>
public sealed class OutagePopupController : IDisposable
{
    private const int MinimumVisibleSeconds = 5;

    private OutageNotificationForm? _form;
    private System.Windows.Forms.Timer? _minVisibleTimer;
    private bool _shownForCurrentEpisode;
    private bool _userClosedCurrentEpisode;
    private bool _minVisibleElapsed;
    private bool _pendingAutoCloseOnRecovery;

    public event EventHandler? RecoveredNotificationRequested;

    public void OnStateChanged(ConnectivityState oldState, ConnectivityState newState)
    {
        if (newState == ConnectivityState.Outage && oldState != ConnectivityState.Outage)
        {
            BeginEpisode();
        }
        else if (newState == ConnectivityState.Connected)
        {
            EndEpisodeOnRecovery();
        }
        // SuspectedOutage transitions intentionally do not touch the popup - a single failed
        // check must never itself trigger a popup.
    }

    private void BeginEpisode()
    {
        ResetEpisodeFlags();
        _shownForCurrentEpisode = true;

        _form = new OutageNotificationForm();
        _form.UserManuallyClosed += (_, _) => _userClosedCurrentEpisode = true;
        _form.FormClosed += (_, _) => _form = null;
        ScreenPositioning.PlaceNearTray(_form);
        _form.Show();

        _minVisibleTimer = new System.Windows.Forms.Timer { Interval = MinimumVisibleSeconds * 1000 };
        _minVisibleTimer.Tick += OnMinimumVisibleElapsed;
        _minVisibleTimer.Start();
    }

    private void OnMinimumVisibleElapsed(object? sender, EventArgs e)
    {
        _minVisibleElapsed = true;
        _minVisibleTimer?.Stop();
        if (_pendingAutoCloseOnRecovery)
        {
            CloseFormIfOpen(notifyRecovered: true);
        }
    }

    private void EndEpisodeOnRecovery()
    {
        if (!_shownForCurrentEpisode)
        {
            return;
        }

        if (_userClosedCurrentEpisode)
        {
            ResetEpisodeFlags();
            return;
        }

        if (_minVisibleElapsed)
        {
            CloseFormIfOpen(notifyRecovered: true);
        }
        else
        {
            _pendingAutoCloseOnRecovery = true;
        }
    }

    private void CloseFormIfOpen(bool notifyRecovered)
    {
        _form?.CloseProgrammatically();
        _form = null;
        if (notifyRecovered)
        {
            RecoveredNotificationRequested?.Invoke(this, EventArgs.Empty);
        }
        ResetEpisodeFlags();
    }

    private void ResetEpisodeFlags()
    {
        _shownForCurrentEpisode = false;
        _userClosedCurrentEpisode = false;
        _minVisibleElapsed = false;
        _pendingAutoCloseOnRecovery = false;
        if (_minVisibleTimer is not null)
        {
            _minVisibleTimer.Stop();
            _minVisibleTimer.Tick -= OnMinimumVisibleElapsed;
            _minVisibleTimer.Dispose();
            _minVisibleTimer = null;
        }
    }

    public void Dispose()
    {
        ResetEpisodeFlags();
        _form?.Dispose();
    }
}
