using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Localization;
using InternetMonitor.Ui.Controls;
using InternetMonitor.Ui.Shell;
using InternetMonitor.Ui.Shell.Pages;
using Xunit;

namespace InternetMonitor.Tests.Ui;

/// <summary>
/// Every page builds without a window, speaks every language without a missing key, fits a narrow
/// and a wide client area, paints, and lets go cleanly. The real layout and localisation code runs;
/// nothing is shown and no dialog is opened. The services are created and disposed on the test
/// thread: disposing them on the UI thread would wait for continuations that need that very thread.
/// </summary>
[Collection("Localization")]
public sealed class PageSmokeTests
{
    private static readonly string[] Languages = ["en", "nl", "de", "pl", "fr", "es", "it", "pt"];

    public static IEnumerable<object[]> Pages() =>
    [
        [typeof(StatusPage)],
        [typeof(DiagnosticsPage)],
        [typeof(HistoryPage)],
        [typeof(IncidentsPage)],
        [typeof(ToolsPage)],
        [typeof(SettingsPage)],
        [typeof(ReportsPage)],
    ];

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task A_page_builds_localises_in_every_language_fits_two_widths_and_paints(Type pageType)
    {
        await using TestServices services = TestServices.Create();
        try
        {
            UiTestHost.Run(() =>
            {
                using var page = (PageBase)Activator.CreateInstance(pageType, services.Services)!;
                page.Activate();

                Assert.Equal(LocalizationManager.Instance.Get($"page.{page.Id}.title"), page.Controls.OfType<PageHeader>().Single().Title);

                foreach (string language in Languages)
                {
                    LocalizationManager.Instance.SetLanguage(language);
                    page.ApplyLocalization();
                    foreach (Control control in UiTestHost.Descendants(page))
                    {
                        Assert.False(control.Text.StartsWith('[') && control.Text.EndsWith(']'), $"{pageType.Name} in {language}: {control.GetType().Name} shows the raw key {control.Text}");
                    }
                }

                foreach (int width in new[] { 500, 1400 })
                {
                    page.Size = new Size(width, 900);
                    page.PerformLayout();
                    foreach (Control child in page.Controls)
                    {
                        Assert.True(child.Right <= page.ClientSize.Width + 1, $"{pageType.Name} at {width}px: {child.GetType().Name} sticks out ({child.Right} > {page.ClientSize.Width})");
                    }
                }

                using var bitmap = new Bitmap(page.Width, page.Height);
                page.DrawToBitmap(bitmap, new Rectangle(Point.Empty, page.Size));

                page.Deactivate();
            });
        }
        finally
        {
            LocalizationManager.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public async Task The_main_window_builds_every_page_and_switches_between_them()
    {
        await using TestServices services = TestServices.Create();
        UiTestHost.Run(() =>
        {
            using var window = new MainWindow(services.Services);
            _ = window.Handle;
            foreach (string id in new[] { "status", "diagnostics", "history", "incidents", "tools", "settings", "reports", "status" })
            {
                window.ShowPage(id);
            }

            window.Size = new Size(900, 700);
            window.PerformLayout();
            Assert.NotEmpty(UiTestHost.Descendants(window).OfType<PageBase>());
        });
    }

    [Fact]
    public async Task Flipping_a_setting_on_the_settings_page_persists_it_and_hides_the_row_that_depends_on_it()
    {
        await using TestServices services = TestServices.Create(new AppSettings { Language = "en", ShowIncidentPopups = true });
        UiTestHost.Run(() =>
        {
            using var page = new SettingsPage(services.Services);
            page.Activate();
            page.Size = new Size(1000, 900);
            page.PerformLayout();

            Toggle popups = Find<Toggle>(page, "settings.showIncidentPopups");
            Control style = Find<Control>(page, "settings.notificationStyle");
            Assert.True(style.Visible);

            InvokeOnClick(popups);

            Assert.False(services.Settings.ShowIncidentPopups);
            Assert.False(AppSettings.Load().ShowIncidentPopups);
            Assert.False(style.Visible);

            InvokeOnClick(popups);
            Assert.True(services.Settings.ShowIncidentPopups);
            Assert.True(style.Visible);
        });
    }

    [Fact]
    public async Task Choosing_the_extended_log_level_reveals_its_two_switches_and_full_hides_them_again()
    {
        await using TestServices services = TestServices.Create(new AppSettings { Language = "en", DiagnosticLogLevel = DiagnosticLogLevel.Off });
        UiTestHost.Run(() =>
        {
            using var page = new SettingsPage(services.Services);
            page.Activate();
            page.Size = new Size(1000, 900);
            page.PerformLayout();

            var level = Find<ComboBox>(page, "settings.diagnostics.level");
            Control successes = Find<Control>(page, "settings.diagnostics.logSuccesses");
            Control folder = Find<Control>(page, "settings.diagnostics.directory");
            Assert.False(successes.Visible);
            Assert.False(folder.Visible);

            Select(level, LocalizationManager.Instance.Get("settings.diagnostics.level.extended"));
            Assert.Equal(DiagnosticLogLevel.Extended, services.Settings.DiagnosticLogLevel);
            Assert.True(successes.Visible);
            Assert.True(folder.Visible);

            Select(level, LocalizationManager.Instance.Get("settings.diagnostics.level.full"));
            Assert.Equal(DiagnosticLogLevel.Full, services.Settings.DiagnosticLogLevel);
            Assert.True(services.Settings.LogSuccessfulChecks && services.Settings.LogDetailedMeasurements);
            Assert.False(successes.Visible);
            Assert.True(folder.Visible);
        });
    }

    [Fact]
    public async Task The_incidents_page_lists_what_the_store_holds()
    {
        await using TestServices services = TestServices.Create();
        services.Services.Incidents.Clear();
        services.Services.Incidents.Add(new Incident
        {
            Id = "INC-20261017-190000.000",
            Cause = RootCause.Internet,
            StartUtc = DateTimeOffset.UtcNow.AddHours(-1),
            EndUtc = DateTimeOffset.UtcNow.AddMinutes(-50),
            Status = IncidentStatus.Resolved,
            Headline = "No public address reachable",
            Throughput = new IncidentThroughput(
                null,
                new ThroughputWindow(DateTimeOffset.UtcNow.AddMinutes(-65), DateTimeOffset.UtcNow.AddHours(-1), 20, 41, 4.1, 140, 14, DateTimeOffset.UtcNow.AddMinutes(-61), 0.2, 0.7),
                null,
                null,
                200,
                20,
                ThroughputCapacitySource.SpeedTest),
        });
        try
        {
            UiTestHost.Run(() =>
            {
                using var page = new IncidentsPage(services.Services);
                page.Activate();
                page.Size = new Size(1000, 900);
                page.PerformLayout();

                Assert.Contains(UiTestHost.Descendants(page), c => c is IncidentList);
                Assert.Single(services.Services.Incidents.All);
                using var bitmap = new Bitmap(page.Width, page.Height);
                page.DrawToBitmap(bitmap, new Rectangle(Point.Empty, page.Size));

                // A store change after the page has gone must not reach it: Deactivate unsubscribes,
                // and the handler itself ignores a page without a handle.
                page.Deactivate();
                services.Services.Incidents.Clear();
            });
        }
        finally
        {
            services.Services.Incidents.Clear();
        }
    }

    private static T Find<T>(Control root, string name) where T : Control =>
        Assert.IsAssignableFrom<T>(Assert.Single(root.Controls.Find(name, searchAllChildren: true)));

    private static void InvokeOnClick(Control control) =>
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [EventArgs.Empty]);

    private static void Select(ComboBox box, string display)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i]!.ToString() == display)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        throw new InvalidOperationException($"No item '{display}' in the combo box.");
    }
}
