using System.Reflection;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Export;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;
using InternetMonitor.Localization;

namespace InternetMonitor.App;

/// <summary>
/// The command line's <c>--export</c>: writes one of the exports the Reports page offers, with no
/// window and no tray, from the same stored settings, history and incidents the running app uses.
/// For a scheduled task or a script - "mail me the provider report every Monday" - which the GUI
/// cannot be asked to do. Runs alongside a running instance; it only reads.
/// </summary>
public static class HeadlessExport
{
    public static readonly IReadOnlyList<string> Kinds = ["zip", "ai", "plain", "report"];

    /// <summary>Writes the export of <paramref name="kind"/> to <paramref name="path"/>; 0 on success, 1 on a bad request or a failure to write.</summary>
    public static async Task<int> RunAsync(string kind, string path)
    {
        kind = kind.Trim().ToLowerInvariant();
        if (!Kinds.Contains(kind))
        {
            Console.Error.WriteLine($"Unknown export kind '{kind}'. Use one of: {string.Join(", ", Kinds)}.");
            return 1;
        }

        AppSettings settings = AppSettings.Load();
        LocalizationManager.Initialize(settings.Language);
        string version = (Assembly.GetExecutingAssembly().GetName().Version ?? new Version(2, 0, 0)).ToString(3);

        await using var history = new MetricStore(TimeSpan.FromDays(settings.HistoryRetentionDays));
        await history.Loaded.ConfigureAwait(false);
        var incidents = new IncidentStore();

        var series = new Dictionary<string, IReadOnlyList<MetricPoint>>();
        foreach (string id in history.SeriesIds.Union(SeriesNames.Chartable.Select(s => s.Id)))
        {
            series[id] = history.Get(id);
        }

        try
        {
            switch (kind)
            {
                case "ai":
                    await File.WriteAllTextAsync(path, AiExportBuilder.Build(incidents.All, series, settings, version)).ConfigureAwait(false);
                    break;
                case "plain":
                    await File.WriteAllTextAsync(path, AiExportBuilder.BuildPlain(incidents.All, series, settings, version)).ConfigureAwait(false);
                    break;
                case "report":
                    await File.WriteAllTextAsync(path, Report(settings, incidents, history, version)).ConfigureAwait(false);
                    break;
                default:
                    byte[]? log = null;
                    string logPath = new DiagnosticLogger(settings).LogPath;
                    if (File.Exists(logPath))
                    {
                        log = await File.ReadAllBytesAsync(logPath).ConfigureAwait(false);
                    }

                    var documents = new Dictionary<string, string>
                    {
                        ["summary.md"] = AiExportBuilder.BuildPlain(incidents.All, series, settings, version),
                        ["provider-report.html"] = Report(settings, incidents, history, version),
                    };
                    await File.WriteAllBytesAsync(path, DataExportBuilder.Build(settings, incidents.All, series, log, documents)).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write {path}: {ex.Message}");
            return 1;
        }

        Console.WriteLine(path);
        return 0;
    }

    /// <summary>The provider report exactly as the Reports page builds it, comparison setting included.</summary>
    private static string Report(AppSettings settings, IncidentStore incidents, MetricStore history, string version)
    {
        DateTimeOffset to = DateTimeOffset.UtcNow;
        DateTimeOffset from = to.AddDays(-settings.ReportPeriodDays);
        ProviderReportData data = ProviderReportData.Compute(incidents.All, history.Get, from, to, TimeZoneInfo.Local);

        ProviderReportData? comparison = null;
        if (ProviderReportHtml.ComparisonWindow(from, to, settings.ReportComparison) is { } window)
        {
            comparison = ProviderReportData.Compute(incidents.All, history.Get, window.From, window.To, TimeZoneInfo.Local);
        }

        return ProviderReportHtml.Render(
            data,
            new ProviderReportOptions(settings.SubscribedDownloadMbps, settings.SubscribedUploadMbps, version, DateTimeOffset.Now, TimeZoneInfo.Local),
            LocalizationManager.Instance,
            comparison,
            settings.ReportComparison);
    }
}
