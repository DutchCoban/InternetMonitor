using Microsoft.Win32;

namespace InternetMonitor.Ui.Theming;

/// <summary>
/// Keeps <see cref="Theme"/> in step with the Windows app theme while the app runs.
///
/// Windows announces a light/dark switch through WM_SETTINGCHANGE("ImmersiveColorSet"), which
/// WinForms already surfaces as <see cref="SystemEvents.UserPreferenceChanged"/> - so there is no
/// need for a message-only window of our own just to receive it. That event is raised on a
/// dedicated system-events thread, so the handler marshals back onto the UI thread before touching
/// <see cref="Theme.Reevaluate"/>: every subscriber of <see cref="Theme.Changed"/> is a control,
/// and a control may only be invalidated from the thread that created it.
///
/// The marshalling target is an explicit <see cref="Control"/> rather than
/// <see cref="SynchronizationContext.Current"/>, because WinForms only installs its
/// synchronization context once a control handle exists - at composition time, before
/// <see cref="Application.Run(ApplicationContext)"/>, Current is still null.
///
/// <see cref="Theme.Reevaluate"/> is cheap, idempotent, and only raises <see cref="Theme.Changed"/>
/// on an actual flip, so the handler can stay this blunt: Windows raises UserPreferenceChanged for
/// plenty of unrelated preferences too.
/// </summary>
public sealed class ThemeWatcher : IDisposable
{
    private readonly Control _uiMarshaller;
    private bool _disposed;

    public ThemeWatcher(Control uiMarshaller)
    {
        _uiMarshaller = uiMarshaller;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color))
        {
            return;
        }

        if (_disposed || _uiMarshaller.IsDisposed || !_uiMarshaller.IsHandleCreated)
        {
            return;
        }

        try
        {
            _uiMarshaller.BeginInvoke(() =>
            {
                if (!_disposed)
                {
                    Theme.Reevaluate();
                }
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The window went away between the check above and the post. Losing a theme update
            // during teardown costs nothing.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
