using InternetMonitor.App;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;

namespace InternetMonitor.Ui.Shell;

/// <summary>
/// Base class for the pages the nav rail switches between.
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
    private bool _settlingScroll;

    /// <summary>
    /// Suppresses WinForms' scroll-into-view while a page is first being shown.
    ///
    /// A ScrollableControl scrolls to whichever child takes focus. On a page taller than the
    /// window that means the first focusable control - a button in the header, a chart - drags the
    /// view down during layout, and the page opens with its own title already scrolled off the
    /// top. Resetting the scroll position does not help, because the focus arrives after it.
    ///
    /// Only the opening moment is suppressed. Once the page has settled, tabbing to an off-screen
    /// control scrolls to it as it should, which matters on the long Settings page.
    /// </summary>
    protected override Point ScrollToControl(Control activeControl) =>
        _settlingScroll ? AutoScrollPosition : base.ScrollToControl(activeControl);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        // By the first paint the layout has run and focus has landed, so normal behaviour can
        // resume.
        _settlingScroll = false;
    }

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

    /// <summary>The page's help balloons - see <see cref="HelpTips"/>.</summary>
    protected HelpTips Tips { get; } = new();

    /// <summary>Gives a control the help text under <paramref name="key"/> + ".help", or none when there is no such text.</summary>
    protected void Tip(Control control, string key) => Tips.Set(Loc.TryGet(key + ".help"), control);

    protected int Dp(int px) => Theme.Dp(px, DeviceDpi);

    protected int Sp(int step) => Theme.Space(step, DeviceDpi);

    /// <summary>A secondary action button inside a card, collected for <see cref="LayoutButtonRow"/>.</summary>
    protected static AppButton AddActionButton(Card card, List<AppButton> buttons, Action onClick)
    {
        var button = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Copy };
        button.Click += (_, _) => onClick();
        card.Controls.Add(button);
        buttons.Add(button);
        return button;
    }

    /// <summary>
    /// Wraps a card's buttons left-to-right onto as many lines as the width needs - the labels vary
    /// a lot in length by language, so a fixed side-by-side row would overflow a narrower window.
    /// Sizes the card to fit; extras placed below start at <see cref="BelowRows"/>.
    /// </summary>
    protected void LayoutButtonRow(Card card, List<AppButton> buttons)
    {
        Rectangle content = card.DisplayRectangle;
        int rowHeight = Dp(32);
        int x = content.Left;
        int y = content.Top;

        foreach (AppButton button in buttons)
        {
            if (x > content.Left && x + button.Width > content.Right)
            {
                x = content.Left;
                y += rowHeight + Sp(1);
            }

            button.Location = new Point(x, y);
            x += button.Width + Sp(2);
        }

        CloseCard(card, y + rowHeight);
    }

    /// <summary>Where hand-placed extras start, under whatever a card's row layout already sized it for.</summary>
    protected static int BelowRows(Card card) => card.Height - card.Inset;

    /// <summary>Sizes a card so its content ends at <paramref name="bottom"/>.</summary>
    protected static void CloseCard(Card card, int bottom) => card.Height = bottom + card.Inset;

    /// <summary>
    /// Builds the page's controls. Called once, the first time the page is shown, so start-up does
    /// not pay for pages the user may never open.
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
        _settlingScroll = true;
        AutoScrollPosition = Point.Empty;

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

        // A page's own fields do not exist until Build() has run, which is deferred to its first
        // Activate() (see the class comment) - a page nobody has opened yet has nothing of its own
        // to re-colour. Skipping OnThemeChanged() here, rather than guarding it in every override,
        // is also what makes the guard impossible to forget on a future page: Build() already runs
        // after the current theme is resolved, so a freshly built page opens with the right colours
        // without ever needing OnThemeChanged called on its behalf.
        if (_built)
        {
            OnThemeChanged();
        }

        Invalidate(true);
    }

    /// <summary>
    /// Hook for pages hosting stock WinForms controls, which do not take part in the theme the way
    /// the custom ones do and have to have their colours assigned explicitly.
    /// </summary>
    protected virtual void OnThemeChanged()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Theme.Changed -= OnThemeChangedInternal;
            Tips.Dispose();
        }

        base.Dispose(disposing);
    }
}
