using InternetMonitor.App;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell;

/// <summary>
/// Base class for the six pages the nav rail switches between.
///
/// Pages are built once and kept alive, not rebuilt on every navigation: a page holds chart state,
/// scroll position and selections the user expects to still be there when they come back. They do
/// get told when they become visible, so a page can subscribe to live events only while it is on
/// screen rather than paying for updates nobody can see - which matters here because the pipeline
/// runs every few seconds.
///
/// A page is a scrolling container (<see cref="ScrollableControl.AutoScroll"/>) so a narrow or
/// short window degrades into a scroll rather than clipping content away, which is how v1's
/// diagnostics window lost its incidents panel on short screens.
/// </summary>
public abstract class PageBase : Panel
{
    private bool _built;

    protected PageBase(AppServices services)
    {
        Services = services;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        BackColor = Theme.Current.Bg;
        Header = new PageHeader();
        Theme.Changed += OnThemeChangedInternal;
    }

    /// <summary>The nav-rail id this page is reached by; also its localization key suffix.</summary>
    public abstract string Id { get; }

    protected PageHeader Header { get; }

    protected AppServices Services { get; }

    protected static LocalizationManager Loc => LocalizationManager.Instance;

    protected int Dp(int px) => Theme.Dp(px, DeviceDpi);

    protected int Sp(int step) => Theme.Space(step, DeviceDpi);

    /// <summary>
    /// Builds the page's controls. Called once, the first time the page is shown, so start-up does
    /// not pay for five pages the user may never open.
    /// </summary>
    protected abstract void Build();

    /// <summary>Called every time the page becomes the visible one. Subscribe to live data here.</summary>
    protected virtual void OnActivated()
    {
    }

    /// <summary>Called when another page takes over. Unsubscribe here.</summary>
    protected virtual void OnDeactivated()
    {
    }

    /// <summary>Re-applies every localized string after a language change.</summary>
    public virtual void ApplyLocalization()
    {
        Header.Title = Loc.Get($"page.{Id}.title");
        Header.Subtitle = Loc.Get($"page.{Id}.subtitle");
    }

    public void Activate()
    {
        if (!_built)
        {
            _built = true;
            Padding = new Padding(Sp(5), Sp(2), Sp(5), Sp(5));

            // Build first, header last. WinForms stacks DockStyle.Top siblings in the order they
            // were added, with the most recently added nearest the top edge - so adding the header
            // first would push it below everything the page adds afterwards.
            Build();
            Controls.Add(Header);
            ApplyLocalization();
        }

        Visible = true;
        OnActivated();
    }

    public void Deactivate()
    {
        Visible = false;
        if (_built)
        {
            OnDeactivated();
        }
    }

    private void OnThemeChangedInternal()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        BackColor = Theme.Current.Bg;
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChangedInternal;
        }

        base.Dispose(disposing);
    }
}
