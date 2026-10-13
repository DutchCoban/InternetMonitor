using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>
/// Settings, in place rather than in its own window - one of thirteen separate forms in v1, here
/// just another page, which is most of why it needed no navigation chrome of its own.
///
/// Scoped to what v2 actually has behind it. Language, theme, auto-start, cadence, the traceroute
/// ownership lookup and the step-7 endpoint list are all real, working settings with something
/// listening on the other end. v1's ping targets, quiet hours, update checking, SLA exclusions,
/// speed test scheduling, diagnostic log levels and Uptime Kuma push are deliberately not here:
/// none of those have a monitor behind them yet in v2, and a setting that controls nothing is
/// worse than a screen that says plainly it is not built yet.
///
/// Every change here applies immediately and saves immediately - no separate "Apply" button,
/// matching v1's live-reconfiguration behaviour. Cadence and the traceroute lookup are read fresh
/// by whatever uses them on their very next cycle; language and theme propagate through the
/// existing LocalizationManager/Theme change events the rest of the app already listens for.
/// </summary>
public sealed class SettingsPage(AppServices services) : PageBase(services)
{
    private Card _generalCard = null!;
    private Card _cadenceCard = null!;
    private Card _toolsCard = null!;
    private Card _diagnosticsCard = null!;
    private Card _integrationsCard = null!;
    private Card _endpointsCard = null!;

    private ComboBox _language = null!;
    private ComboBox _theme = null!;
    private Toggle _autoStart = null!;
    private Toggle _showIncidentPopups = null!;
    private NumericUpDown _foregroundInterval = null!;
    private NumericUpDown _backgroundInterval = null!;
    private Toggle _tracerouteLookup = null!;
    private ComboBox _logLevel = null!;
    private Toggle _logSuccessfulChecks = null!;
    private Toggle _logDetailedMeasurements = null!;
    private TextBox _logDirectory = null!;
    private AppButton _browseLogDirectory = null!;
    private AppButton _openLogFolder = null!;
    private AppButton _resetLogDirectory = null!;
    private TextBox _kumaUrl = null!;
    private NumericUpDown _kumaInterval = null!;
    private Label _kumaError = null!;
    private AppButton _kumaAbout = null!;
    private EndpointList _endpoints = null!;
    private AppButton _addEndpoint = null!;

    private readonly List<(Label Label, Control Field)> _rows = [];

    public override string Id => "settings";

    protected override void Build()
    {
        _generalCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Settings };
        BuildGeneralRows();

        _cadenceCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Refresh };
        BuildCadenceRows();

        _toolsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Tools };
        BuildToolsRows();

        _diagnosticsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Diagnostics };
        BuildDiagnosticsRows();

        _integrationsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Globe };
        BuildIntegrationsRows();

        _endpointsCard = new Card { Dock = DockStyle.Top, HeaderGlyph = Glyph.Target };
        BuildEndpointsSection();

        // Reverse visual order: DockStyle.Top stacks later siblings nearer the top edge.
        Controls.Add(_endpointsCard);
        Controls.Add(_integrationsCard);
        Controls.Add(_diagnosticsCard);
        Controls.Add(_toolsCard);
        Controls.Add(_cadenceCard);
        Controls.Add(_generalCard);

        LoadFromSettings();
        LayoutCards();
    }

    // ---- General ------------------------------------------------------------------------------

    private void BuildGeneralRows()
    {
        _language = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        foreach ((string code, string displayName) in LocalizationManager.SupportedLanguages)
        {
            _language.Items.Add(new ComboItem(code, displayName));
        }

        _language.SelectedIndexChanged += OnLanguageChanged;
        AddRow(_generalCard, "settings.language", _language);

        _theme = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _theme.SelectedIndexChanged += OnThemeSelectionChanged;
        AddRow(_generalCard, "settings.theme", _theme);

        _autoStart = new Toggle();
        _autoStart.CheckedChanged += OnAutoStartChanged;
        AddRow(_generalCard, "settings.autoStart", _autoStart);

        _showIncidentPopups = new Toggle();
        _showIncidentPopups.CheckedChanged += (_, _) =>
        {
            Services.Settings.ShowIncidentPopups = _showIncidentPopups.Checked;
            Services.Settings.Save();
        };
        AddRow(_generalCard, "settings.showIncidentPopups", _showIncidentPopups);
    }

    // ---- Cadence ------------------------------------------------------------------------------

    private void BuildCadenceRows()
    {
        _foregroundInterval = MakeIntervalField(1, 10);
        _foregroundInterval.ValueChanged += (_, _) =>
        {
            Services.Settings.ForegroundIntervalSeconds = (int)_foregroundInterval.Value;
            Services.Settings.Save();
        };
        AddRow(_cadenceCard, "settings.foregroundInterval", _foregroundInterval);

        _backgroundInterval = MakeIntervalField(5, 300);
        _backgroundInterval.ValueChanged += (_, _) =>
        {
            int seconds = (int)_backgroundInterval.Value;
            Services.Settings.BackgroundIntervalSeconds = seconds;
            Services.Settings.Save();

            // History is deliberately sampled at the background rate regardless of how fast the
            // pipeline is actually running (see MetricStore's own comment on why) - changing this
            // setting is the one place that rate itself should also change, live.
            Services.History.MinimumSampleInterval = TimeSpan.FromSeconds(seconds);
        };
        AddRow(_cadenceCard, "settings.backgroundInterval", _backgroundInterval);
    }

    private NumericUpDown MakeIntervalField(int min, int max) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = Dp(70),
        TextAlign = HorizontalAlignment.Right,
    };

    // ---- Tools --------------------------------------------------------------------------------

    private void BuildToolsRows()
    {
        _tracerouteLookup = new Toggle();
        _tracerouteLookup.CheckedChanged += (_, _) =>
        {
            Services.Settings.TracerouteOwnerLookup = _tracerouteLookup.Checked;
            Services.Settings.Save();
        };
        AddRow(_toolsCard, "settings.tracerouteLookup", _tracerouteLookup);
    }

    // ---- Diagnostics --------------------------------------------------------------------------

    private void BuildDiagnosticsRows()
    {
        _logLevel = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Dp(160) };
        _logLevel.SelectedIndexChanged += OnLogLevelChanged;
        AddRow(_diagnosticsCard, "settings.diagnostics.level", _logLevel);

        _logSuccessfulChecks = new Toggle();
        _logSuccessfulChecks.CheckedChanged += (_, _) =>
        {
            Services.Settings.LogSuccessfulChecks = _logSuccessfulChecks.Checked;
            Services.Settings.Save();
        };
        AddRow(_diagnosticsCard, "settings.diagnostics.logSuccesses", _logSuccessfulChecks);

        _logDetailedMeasurements = new Toggle();
        _logDetailedMeasurements.CheckedChanged += (_, _) =>
        {
            Services.Settings.LogDetailedMeasurements = _logDetailedMeasurements.Checked;
            Services.Settings.Save();
        };
        AddRow(_diagnosticsCard, "settings.diagnostics.logDetails", _logDetailedMeasurements);

        _logDirectory = new TextBox
        {
            BorderStyle = BorderStyle.FixedSingle,
            Width = Dp(200),
            Font = Theme.Font(FontRole.Body),
            ReadOnly = true,
        };
        AddRow(_diagnosticsCard, "settings.diagnostics.directory", _logDirectory);

        _browseLogDirectory = new AppButton { Variant = ButtonVariant.Secondary, Text = Loc.Get("settings.diagnostics.browse") };
        _browseLogDirectory.SizeToContent();
        _browseLogDirectory.Click += OnBrowseLogDirectoryClicked;
        _diagnosticsCard.Controls.Add(_browseLogDirectory);

        _openLogFolder = new AppButton { Variant = ButtonVariant.Ghost, Text = Loc.Get("settings.diagnostics.openFolder") };
        _openLogFolder.SizeToContent();
        _openLogFolder.Click += (_, _) => OpenLogFolder();
        _diagnosticsCard.Controls.Add(_openLogFolder);

        _resetLogDirectory = new AppButton { Variant = ButtonVariant.Ghost, Text = Loc.Get("settings.diagnostics.resetDirectory") };
        _resetLogDirectory.SizeToContent();
        _resetLogDirectory.Click += (_, _) =>
        {
            Services.Settings.DiagnosticLogDirectory = string.Empty;
            Services.Settings.Save();
            RefreshLogDirectoryText();
        };
        _diagnosticsCard.Controls.Add(_resetLogDirectory);
    }

    private void OnLogLevelChanged(object? sender, EventArgs e)
    {
        if (_logLevel.SelectedItem is not ComboItem item || !Enum.TryParse(item.Key, out DiagnosticLogLevel level))
        {
            return;
        }

        Services.Settings.DiagnosticLogLevel = level;
        Services.Settings.Save();
    }

    private void OnBrowseLogDirectoryClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            SelectedPath = Services.DiagnosticLog.ResolvedDirectory(),
            Description = Loc.Get("settings.diagnostics.browse"),
        };

        if (dialog.ShowDialog(FindForm()) != DialogResult.OK)
        {
            return;
        }

        Services.Settings.DiagnosticLogDirectory = dialog.SelectedPath;
        Services.Settings.Save();
        RefreshLogDirectoryText();
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Services.DiagnosticLog.ResolvedDirectory()) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The folder does not exist yet (nothing has logged there) or no shell handler is
            // registered - not worth surfacing for a convenience shortcut the user can open
            // themselves via Explorer.
        }
    }

    private void RefreshLogDirectoryText() => _logDirectory.Text = Services.DiagnosticLog.ResolvedDirectory();

    // ---- Integrations ---------------------------------------------------------------------------

    private void BuildIntegrationsRows()
    {
        _kumaUrl = new TextBox { BorderStyle = BorderStyle.FixedSingle, Width = Dp(200), Font = Theme.Font(FontRole.Body) };
        _kumaUrl.Leave += OnKumaSettingsChanged;
        AddRow(_integrationsCard, "settings.kuma.pushUrl", _kumaUrl);

        _kumaInterval = MakeIntervalField(KumaPusher.MinimumIntervalSeconds, 86400);
        _kumaInterval.ValueChanged += OnKumaSettingsChanged;
        AddRow(_integrationsCard, "settings.kuma.interval", _kumaInterval);

        _kumaError = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };
        _integrationsCard.Controls.Add(_kumaError);

        _kumaAbout = new AppButton { Variant = ButtonVariant.Ghost, Glyph = Glyph.Globe, Text = Loc.Get("settings.kuma.about") };
        _kumaAbout.SizeToContent();
        _kumaAbout.Click += (_, _) => OpenUptimeKumaProjectPage();
        _integrationsCard.Controls.Add(_kumaAbout);
    }

    private static void OpenUptimeKumaProjectPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/louislam/uptime-kuma") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser configured, or the shell call otherwise failed - not worth
            // surfacing for an informational link the user can open themselves.
        }
    }

    private void OnKumaSettingsChanged(object? sender, EventArgs e)
    {
        string url = _kumaUrl.Text.Trim();

        // Empty is valid - it means the integration is off. Otherwise require a well-formed https
        // URL, the same standard endpoint targets enforce, so a typo or plaintext http:// URL
        // doesn't silently reach KumaPusher.
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != Uri.UriSchemeHttps))
        {
            _kumaError.Text = Loc.Get("settings.kuma.url.error");
            return;
        }

        _kumaError.Text = string.Empty;
        Services.Settings.KumaPushUrl = url;
        Services.Settings.KumaIntervalSeconds = (int)_kumaInterval.Value;
        Services.Settings.Save();
        Services.Kuma.Reconfigure(Services.Settings.KumaPushUrl, Services.Settings.KumaIntervalSeconds);
    }

    // ---- Endpoints ----------------------------------------------------------------------------

    private void BuildEndpointsSection()
    {
        _endpoints = new EndpointList { Dock = DockStyle.Top };
        _endpoints.EditRequested += OnEndpointEditRequested;
        _endpoints.ToggleChanged += OnEndpointToggled;
        _endpoints.RemoveRequested += OnEndpointRemoved;

        _addEndpoint = new AppButton { Variant = ButtonVariant.Secondary, Glyph = Glyph.Target, Dock = DockStyle.Top };
        _addEndpoint.Click += (_, _) => OpenEndpointEditor(null);

        // Fill before Top within this card: the button sits above a list whose height tracks the
        // number of endpoints, so the button is added last to land above it.
        _endpointsCard.Controls.Add(_endpoints);
        _endpointsCard.Controls.Add(_addEndpoint);
    }

    private void OnEndpointEditRequested(EndpointConfig endpoint) => OpenEndpointEditor(endpoint);

    private void OpenEndpointEditor(EndpointConfig? existing)
    {
        using EndpointEditWindow window = existing is null ? EndpointEditWindow.ForNew() : EndpointEditWindow.ForEdit(existing);
        if (window.ShowDialog(FindForm()) != DialogResult.OK || window.Result is not { } result)
        {
            return;
        }

        List<EndpointConfig> endpoints = Services.Settings.ApplicationEndpoints;
        int index = endpoints.FindIndex(e => e.Id == result.Id);
        if (index >= 0)
        {
            endpoints[index] = result;
        }
        else
        {
            endpoints.Add(result);
        }

        Services.Settings.Save();
        RefreshEndpoints();
    }

    private void OnEndpointToggled(EndpointConfig updated)
    {
        List<EndpointConfig> endpoints = Services.Settings.ApplicationEndpoints;
        int index = endpoints.FindIndex(e => e.Id == updated.Id);
        if (index < 0)
        {
            return;
        }

        endpoints[index] = updated;
        Services.Settings.Save();
    }

    private void OnEndpointRemoved(EndpointConfig endpoint)
    {
        Services.Settings.ApplicationEndpoints.RemoveAll(e => e.Id == endpoint.Id);
        Services.Settings.Save();
        RefreshEndpoints();
    }

    private void RefreshEndpoints()
    {
        _endpoints.SetEndpoints(Services.Settings.ApplicationEndpoints);
        ResizeEndpointsList();
    }

    private void ResizeEndpointsList()
    {
        _endpoints.Height = _endpoints.GetPreferredSize(Size.Empty).Height;
        LayoutCards();
    }

    // ---- Shared row layout ----------------------------------------------------------------------

    /// <summary>
    /// Adds one label/control row to a settings card. Cards size themselves from their rows, so a
    /// longer translation grows the card instead of clipping the control beside it.
    /// </summary>
    private void AddRow(Card card, string labelKey, Control field)
    {
        var label = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Font = Theme.Font(FontRole.Body) };
        card.Controls.Add(label);
        card.Controls.Add(field);
        _rows.Add((label, field));
        label.Tag = labelKey;
    }

    private int RowHeight => Dp(36);

    /// <summary>
    /// Positions one card's label/field rows and resizes the card to fit them. Bounds set on a
    /// manually-positioned child are always relative to the parent control's own origin (0,0) -
    /// unlike Dock/Anchor layout, WinForms does not consult a custom DisplayRectangle override for
    /// them - so every row has to be offset by the card's own DisplayRectangle explicitly, or it
    /// lands at the card's top-left and sits directly on top of the title instead of below it.
    /// </summary>
    private void LayoutCardRows(Card card)
    {
        List<(Label Label, Control Field)> rows = [.. _rows.Where(r => r.Label.Parent == card)];
        Rectangle content = card.DisplayRectangle;
        int y = content.Top;
        int width = Math.Max(Dp(200), content.Width);

        foreach ((Label label, Control field) in rows)
        {
            label.Bounds = new Rectangle(content.Left, y, Math.Max(0, width - field.Width - Sp(3)), RowHeight);
            field.Location = new Point(content.Left + width - field.Width, y + ((RowHeight - field.Height) / 2));
            y += RowHeight;
        }

        card.Height = y + card.Inset;
    }

    private void LayoutCards()
    {
        LayoutCardRows(_generalCard);
        LayoutCardRows(_cadenceCard);
        LayoutCardRows(_toolsCard);
        LayoutCardRows(_diagnosticsCard);
        LayoutCardRows(_integrationsCard);

        // LayoutCardRows sized the card to its three label/field rows; the directory buttons sit
        // below those, in the space the row layout does not know about - same pattern as the Kuma
        // extras just below.
        Rectangle diagnosticsContent = _diagnosticsCard.DisplayRectangle;
        int diagnosticsY = _diagnosticsCard.Height - _diagnosticsCard.Inset;
        _browseLogDirectory.Location = new Point(diagnosticsContent.Right - _browseLogDirectory.Width, diagnosticsY);
        _openLogFolder.Location = new Point(_browseLogDirectory.Left - Sp(2) - _openLogFolder.Width, diagnosticsY);
        _resetLogDirectory.Location = new Point(_openLogFolder.Left - Sp(2) - _resetLogDirectory.Width, diagnosticsY);
        diagnosticsY += Math.Max(_browseLogDirectory.Height, Math.Max(_openLogFolder.Height, _resetLogDirectory.Height));
        _diagnosticsCard.Height = diagnosticsY + _diagnosticsCard.Inset;

        // LayoutCardRows sized the card to its two label/field rows; the error label and the
        // "about" link sit below those, in the space the row layout does not know about.
        Rectangle integrationsContent = _integrationsCard.DisplayRectangle;
        int integrationsY = _integrationsCard.Height - _integrationsCard.Inset;
        _kumaError.Bounds = new Rectangle(integrationsContent.Left, integrationsY, integrationsContent.Width, Dp(16));
        integrationsY += Dp(18);
        _kumaAbout.Location = new Point(integrationsContent.Left, integrationsY);
        integrationsY += _kumaAbout.Height;
        _integrationsCard.Height = integrationsY + _integrationsCard.Inset;

        Rectangle content = _endpointsCard.DisplayRectangle;
        _addEndpoint.Width = content.Width;
        _endpoints.Width = content.Width;

        int contentHeight = _addEndpoint.Height + Sp(2) + _endpoints.Height;
        _endpointsCard.Height = content.Top + contentHeight + _endpointsCard.Inset;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutCards();
    }

    // ---- Loading / applying settings ------------------------------------------------------------

    private void LoadFromSettings()
    {
        AppSettings settings = Services.Settings;

        _language.SelectedIndexChanged -= OnLanguageChanged;
        SelectComboItem(_language, settings.Language);
        _language.SelectedIndexChanged += OnLanguageChanged;

        _theme.SelectedIndexChanged -= OnThemeSelectionChanged;
        PopulateThemeItems();
        SelectComboItem(_theme, settings.Theme.ToString());
        _theme.SelectedIndexChanged += OnThemeSelectionChanged;

        _autoStart.CheckedChanged -= OnAutoStartChanged;
        _autoStart.SetWithoutNotify(App.AutoStart.IsEnabled());
        _autoStart.CheckedChanged += OnAutoStartChanged;

        _showIncidentPopups.SetWithoutNotify(settings.ShowIncidentPopups);

        _foregroundInterval.Value = settings.ForegroundIntervalSeconds;
        _backgroundInterval.Value = settings.BackgroundIntervalSeconds;

        _tracerouteLookup.SetWithoutNotify(settings.TracerouteOwnerLookup);

        _logLevel.SelectedIndexChanged -= OnLogLevelChanged;
        PopulateLogLevelItems();
        SelectComboItem(_logLevel, settings.DiagnosticLogLevel.ToString());
        _logLevel.SelectedIndexChanged += OnLogLevelChanged;

        _logSuccessfulChecks.SetWithoutNotify(settings.LogSuccessfulChecks);
        _logDetailedMeasurements.SetWithoutNotify(settings.LogDetailedMeasurements);
        RefreshLogDirectoryText();

        _kumaUrl.Leave -= OnKumaSettingsChanged;
        _kumaInterval.ValueChanged -= OnKumaSettingsChanged;
        _kumaUrl.Text = settings.KumaPushUrl;
        _kumaInterval.Value = Math.Clamp(settings.KumaIntervalSeconds, KumaPusher.MinimumIntervalSeconds, 86400);
        _kumaUrl.Leave += OnKumaSettingsChanged;
        _kumaInterval.ValueChanged += OnKumaSettingsChanged;

        RefreshEndpoints();
    }

    private void PopulateThemeItems()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _theme.Items.Clear();
        _theme.Items.Add(new ComboItem(nameof(ThemeMode.System), loc.Get("settings.theme.system")));
        _theme.Items.Add(new ComboItem(nameof(ThemeMode.Light), loc.Get("settings.theme.light")));
        _theme.Items.Add(new ComboItem(nameof(ThemeMode.Dark), loc.Get("settings.theme.dark")));
    }

    private void PopulateLogLevelItems()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        _logLevel.Items.Clear();
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Off), loc.Get("settings.diagnostics.level.off")));
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Basic), loc.Get("settings.diagnostics.level.basic")));
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Extended), loc.Get("settings.diagnostics.level.extended")));
        _logLevel.Items.Add(new ComboItem(nameof(DiagnosticLogLevel.Full), loc.Get("settings.diagnostics.level.full")));
    }

    private static void SelectComboItem(ComboBox box, string key)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboItem item && item.Key == key)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_language.SelectedItem is not ComboItem item)
        {
            return;
        }

        Services.Settings.Language = item.Key;
        Services.Settings.Save();

        // Raises LanguageChanged, which MainWindow already listens for and uses to re-localize
        // every page including this one - ApplyLocalization() below is what repopulates the theme
        // combo's translated labels and the "no endpoints yet" placeholder text.
        LocalizationManager.Instance.SetLanguage(item.Key);
    }

    private void OnThemeSelectionChanged(object? sender, EventArgs e)
    {
        if (_theme.SelectedItem is not ComboItem item || !Enum.TryParse(item.Key, out ThemeMode mode))
        {
            return;
        }

        Services.Settings.Theme = mode;
        Services.Settings.Save();
        Theme.Mode = mode;
    }

    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        bool wanted = _autoStart.Checked;
        bool applied = App.AutoStart.SetEnabled(wanted);

        Services.Settings.AutoStartWithWindows = applied && wanted;
        Services.Settings.Save();

        if (applied != true && wanted)
        {
            // The registry write failed (locked-down machine, say) - show the truth rather than a
            // toggle that claims success it could not deliver.
            _autoStart.CheckedChanged -= OnAutoStartChanged;
            _autoStart.SetWithoutNotify(false);
            _autoStart.CheckedChanged += OnAutoStartChanged;
        }
    }

    public override void ApplyLocalization()
    {
        base.ApplyLocalization();

        if (_generalCard is null)
        {
            return;
        }

        _generalCard.Title = Loc.Get("settings.section.general");
        _cadenceCard.Title = Loc.Get("settings.section.cadence");
        _cadenceCard.Subtitle = Loc.Get("settings.section.cadence.subtitle");
        _toolsCard.Title = Loc.Get("settings.section.tools");
        _diagnosticsCard.Title = Loc.Get("settings.section.diagnostics");
        _diagnosticsCard.Subtitle = Loc.Get("settings.section.diagnostics.subtitle");
        _integrationsCard.Title = Loc.Get("settings.section.integrations");
        _integrationsCard.Subtitle = Loc.Get("settings.section.integrations.subtitle");
        _endpointsCard.Title = Loc.Get("settings.section.endpoints");
        _endpointsCard.Subtitle = Loc.Get("settings.section.endpoints.subtitle");

        foreach ((Label label, _) in _rows)
        {
            if (label.Tag is string key)
            {
                label.Text = Loc.Get(key);
            }
        }

        string previousLanguage = (_language.SelectedItem as ComboItem)?.Key ?? Services.Settings.Language;
        string previousTheme = (_theme.SelectedItem as ComboItem)?.Key ?? Services.Settings.Theme.ToString();
        string previousLogLevel = (_logLevel.SelectedItem as ComboItem)?.Key ?? Services.Settings.DiagnosticLogLevel.ToString();

        PopulateThemeItems();
        SelectComboItem(_theme, previousTheme);
        SelectComboItem(_language, previousLanguage);

        PopulateLogLevelItems();
        SelectComboItem(_logLevel, previousLogLevel);

        _addEndpoint.Text = Loc.Get("settings.endpoint.add");
        _addEndpoint.SizeToContent();

        _browseLogDirectory.Text = Loc.Get("settings.diagnostics.browse");
        _browseLogDirectory.SizeToContent();
        _openLogFolder.Text = Loc.Get("settings.diagnostics.openFolder");
        _openLogFolder.SizeToContent();
        _resetLogDirectory.Text = Loc.Get("settings.diagnostics.resetDirectory");
        _resetLogDirectory.SizeToContent();

        _kumaAbout.Text = Loc.Get("settings.kuma.about");
        _kumaAbout.SizeToContent();

        RefreshEndpoints();
        ApplyControlColors();
        LayoutCards();
    }

    protected override void OnThemeChanged() => ApplyControlColors();

    private void ApplyControlColors()
    {
        Palette p = Theme.Current;

        foreach ((Label label, _) in _rows)
        {
            label.ForeColor = p.TextSecondary;
            label.BackColor = p.Surface;
        }

        foreach (ComboBox box in new[] { _language, _theme, _logLevel })
        {
            box.BackColor = p.SurfaceAlt;
            box.ForeColor = p.TextPrimary;
            box.FlatStyle = FlatStyle.Flat;
        }

        foreach (NumericUpDown field in new[] { _foregroundInterval, _backgroundInterval, _kumaInterval })
        {
            field.BackColor = p.SurfaceAlt;
            field.ForeColor = p.TextPrimary;
        }

        _logDirectory.BackColor = p.SurfaceAlt;
        _logDirectory.ForeColor = p.TextPrimary;

        _kumaUrl.BackColor = p.SurfaceAlt;
        _kumaUrl.ForeColor = p.TextPrimary;
        _kumaError.BackColor = p.Surface;
        _kumaError.ForeColor = p.Error;
    }

    /// <summary>A combo-box entry carrying both the stored key and the text shown to the user.</summary>
    private sealed record ComboItem(string Key, string Display)
    {
        public override string ToString() => Display;
    }
}
