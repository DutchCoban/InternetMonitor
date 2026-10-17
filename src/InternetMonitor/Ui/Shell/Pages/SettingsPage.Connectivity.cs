using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Monitors;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Theming;
using InternetMonitor.Ui.Windows;

namespace InternetMonitor.Ui.Shell.Pages;

/// <summary>The settings page's publicinternetstepsand, integrations section(s); see SettingsPage.cs for the fields and the shared layout.</summary>
public sealed partial class SettingsPage
{
    // ---- Public internet (steps 5 and 6) --------------------------------------------------------

    private void BuildPublicIpRows()
    {
        _publicIpTargets = MakeAddressField();
        AddRow(_publicIpCard, "settings.publicIp.targets", _publicIpTargets);

        _customDnsServers = MakeAddressField();
        AddRow(_publicIpCard, "settings.publicIp.customDns", _customDnsServers);

        _publicIpError = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };
        _publicIpCard.Controls.Add(_publicIpError);
    }

    private TextBox MakeAddressField()
    {
        var box = new TextBox { BorderStyle = BorderStyle.FixedSingle, Width = Dp(260), Font = Theme.Font(FontRole.Body) };
        box.Leave += OnPublicAddressesChanged;
        return box;
    }

    /// <summary>
    /// Validates both address fields together, saves whichever is valid, and shows the first
    /// error - checking both on every edit keeps the one error line honest about a field that is
    /// still wrong after the other one was fixed.
    /// </summary>
    private void OnPublicAddressesChanged(object? sender, EventArgs e)
    {
        string? targetsError = ApplyAddressField(_publicIpTargets, list => Services.Settings.PublicIpTargets = list, allowEmpty: false, maxCount: null);
        string? customError = ApplyAddressField(_customDnsServers, list => Services.Settings.CustomDnsServers = list, allowEmpty: true, maxCount: AppSettings.MaxCustomDnsServers);
        _publicIpError.Text = targetsError ?? customError ?? string.Empty;
        Services.Settings.Save();
    }

    /// <summary>
    /// Accepts a comma-, semicolon- or space-separated list of IPv4 addresses. Any entry that is
    /// not one rejects the field as a whole rather than being silently dropped, so what is saved
    /// is always exactly what the field shows. Returns the error to show, or null once stored.
    /// </summary>
    private string? ApplyAddressField(TextBox box, Action<List<string>> store, bool allowEmpty, int? maxCount)
    {
        string[] entries = box.Text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0 && !allowEmpty)
        {
            return Loc.Get("settings.publicIp.error.empty");
        }

        if (entries.FirstOrDefault(entry => AppSettings.ParseIPv4List([entry]).Count == 0) is { } invalid)
        {
            return Loc.Format("settings.publicIp.error.invalid", invalid);
        }

        List<string> normalized = [.. AppSettings.ParseIPv4List(entries).Select(a => a.ToString())];
        if (maxCount is { } max && normalized.Count > max)
        {
            return Loc.Format("settings.publicIp.error.tooMany", max);
        }

        store(normalized);
        box.Text = string.Join(", ", normalized);
        return null;
    }

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

        // What the last push did - the one thing a push integration is otherwise silent about.
        _kumaStatus = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.Font(FontRole.Small),
            Text = string.Empty,
        };
        _integrationsCard.Controls.Add(_kumaStatus);
        Services.Kuma.PushCompleted += OnKumaPushCompleted;
        Disposed += (_, _) => Services.Kuma.PushCompleted -= OnKumaPushCompleted;

        _kumaAbout = new AppButton { Variant = ButtonVariant.Ghost, Glyph = Glyph.Globe, Text = Loc.Get("settings.kuma.about") };
        _kumaAbout.SizeToContent();
        _kumaAbout.Click += (_, _) => OpenUptimeKumaProjectPage();
        _integrationsCard.Controls.Add(_kumaAbout);
    }

    private static void OpenUptimeKumaProjectPage() => ShellOpen.OpenUrl("https://github.com/louislam/uptime-kuma");

    /// <summary>Arrives on the pusher's loop; the label is touched on the UI thread only.</summary>
    private void OnKumaPushCompleted(KumaPushStatus status)
    {
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(() => RefreshKumaStatus(status));
        }
    }

    private void RefreshKumaStatus(KumaPushStatus? status)
    {
        if (_kumaStatus is null)
        {
            return;
        }

        _kumaStatus.Text = status is null
            ? Services.Settings.KumaPushUrl.Length > 0 ? Loc.Get("settings.kuma.status.none") : string.Empty
            : Loc.Format(
                "settings.kuma.status",
                status.AtUtc.ToLocalTime().ToString("HH:mm:ss"),
                status.Failed ? Loc.Format("settings.kuma.status.failed", status.Detail) : Loc.Get("settings.kuma.status.ok"));
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
}
