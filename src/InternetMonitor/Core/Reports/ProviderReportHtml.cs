using System.Globalization;
using System.Net;
using System.Text;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Reports;

/// <summary>What the person making the report filled in, and what to stamp on it.</summary>
/// <param name="SubscribedDownloadMbps">The subscription's advertised download speed, or 0 when not entered.</param>
public sealed record ProviderReportOptions(
    double SubscribedDownloadMbps,
    double SubscribedUploadMbps,
    string AppVersion,
    DateTimeOffset GeneratedAt,
    TimeZoneInfo TimeZone);

/// <summary>
/// Renders <see cref="ProviderReportData"/> as one self-contained HTML page: inline CSS, charts as
/// inline SVG, no scripts and nothing fetched from anywhere - it has to open the same way from a
/// mail attachment as from disk, and print cleanly to PDF from any browser.
///
/// It is written for someone at the provider's help desk as much as for the user: what was
/// measured, how, and how much of the period the measurements cover, before any conclusion. It
/// deliberately carries no identifying detail of the network - no addresses, network names or
/// hardware identifiers - only times, durations and speeds.
///
/// Everything that came from data is HTML-encoded; nothing in here trusts a string to be markup.
/// </summary>
public static class ProviderReportHtml
{
    private const double BelowPlanThreshold = 0.8;

    public static string Render(ProviderReportData data, ProviderReportOptions options, LocalizationManager loc)
    {
        var html = new StringBuilder();
        string E(string text) => WebUtility.HtmlEncode(text);
        string Local(DateTimeOffset utc, string format) => TimeZoneInfo.ConvertTime(utc, options.TimeZone).ToString(format, CultureInfo.InvariantCulture);

        html.Append("<!doctype html><html lang=\"").Append(E(loc.CurrentLanguage)).Append("\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(E(loc.Get("report.p.title"))).Append("</title>")
            .Append("<style>").Append(Css).Append("</style></head><body><main>");

        // ---- Header ----
        html.Append("<header><h1>").Append(E(loc.Get("report.p.title"))).Append("</h1>")
            .Append("<p class=\"meta\">").Append(E(loc.Format("report.p.period", Local(data.FromUtc, "dd-MM-yyyy HH:mm"), Local(data.ToUtc, "dd-MM-yyyy HH:mm")))).Append("<br>")
            .Append(E(loc.Format("report.p.generated", Local(options.GeneratedAt, "dd-MM-yyyy HH:mm"), options.AppVersion))).Append("</p>")
            .Append("<p>").Append(E(loc.Get("report.p.intro"))).Append("</p></header>");

        // ---- Summary ----
        List<OutageEntry> provider = [.. data.ProviderOutages];
        TimeSpan providerTotal = provider.Aggregate(TimeSpan.Zero, (sum, o) => sum + o.Duration);
        int belowPlan = options.SubscribedDownloadMbps > 0
            ? data.SpeedTests.Count(s => s.DownloadMbps < options.SubscribedDownloadMbps * BelowPlanThreshold)
            : 0;

        html.Append("<section><h2>").Append(E(loc.Get("report.p.summary"))).Append("</h2><div class=\"tiles\">");
        Tile(html, loc.Get("report.p.availability"), data.Availability is { } a ? Percent(a, 2) : "-", loc.Get("report.p.availability.note"));
        Tile(html, loc.Get("report.p.providerOutages"), provider.Count.ToString(CultureInfo.InvariantCulture), loc.Format("report.p.totalDuration", Duration(providerTotal, loc)));
        Tile(html, loc.Get("report.p.longest"), provider.Count > 0 ? Duration(provider.Max(o => o.Duration), loc) : "-", null);
        Tile(html, loc.Get("report.p.coverage"), Percent(data.Coverage, data.Coverage < 0.1 ? 1 : 0), loc.Format("report.p.coverage.note", Duration(data.Monitored, loc)));
        if (options.SubscribedDownloadMbps > 0 && data.SpeedTests.Count > 0)
        {
            Tile(html, loc.Get("report.p.belowPlan"), loc.Format("report.p.xOfY", belowPlan, data.SpeedTests.Count), loc.Get("report.p.belowPlan.note"));
        }

        html.Append("</div>");
        int otherCount = data.Outages.Count - provider.Count;
        if (otherCount > 0)
        {
            html.Append("<p class=\"note\">").Append(E(loc.Format("report.p.otherOutages", otherCount))).Append("</p>");
        }

        html.Append("</section>");

        // ---- Outages ----
        html.Append("<section><h2>").Append(E(loc.Get("report.p.outages"))).Append("</h2>");
        if (data.Outages.Count == 0)
        {
            html.Append("<p>").Append(E(loc.Get("report.p.noOutages"))).Append("</p>");
        }
        else
        {
            html.Append("<h3>").Append(E(loc.Get("report.p.byCause"))).Append("</h3><table><thead><tr>")
                .Append(Th(loc.Get("report.p.col.cause"))).Append(Th(loc.Get("report.p.col.side")))
                .Append(Th(loc.Get("report.p.col.count"), true)).Append(Th(loc.Get("report.p.col.total"), true)).Append(Th(loc.Get("report.p.col.longest"), true))
                .Append("</tr></thead><tbody>");
            foreach (OutageGroup group in data.OutagesByCause)
            {
                html.Append("<tr>").Append(Td(loc.Get($"cause.{group.Cause.Key()}"))).Append(Td(SideLabel(group.Side, loc)))
                    .Append(Td(group.Count.ToString(CultureInfo.InvariantCulture), true)).Append(Td(Duration(group.Total, loc), true)).Append(Td(Duration(group.Longest, loc), true))
                    .Append("</tr>");
            }

            html.Append("</tbody></table>");

            html.Append("<h3>").Append(E(loc.Get("report.p.allOutages"))).Append("</h3><table><thead><tr>")
                .Append(Th(loc.Get("report.p.col.start"))).Append(Th(loc.Get("report.p.col.duration"), true))
                .Append(Th(loc.Get("report.p.col.cause"))).Append(Th(loc.Get("report.p.col.side")))
                .Append("</tr></thead><tbody>");
            foreach (OutageEntry outage in data.Outages)
            {
                string duration = Duration(outage.Duration, loc) + (outage.Ongoing ? " (" + loc.Get("report.p.ongoing") + ")" : string.Empty);
                html.Append(outage.Side == OutageSide.Provider ? "<tr class=\"provider\">" : "<tr>")
                    .Append(Td(Local(outage.StartUtc, "dd-MM-yyyy HH:mm:ss"))).Append(Td(duration, true))
                    .Append(Td(loc.Get($"cause.{outage.Cause.Key()}"))).Append(Td(SideLabel(outage.Side, loc)))
                    .Append("</tr>");
            }

            html.Append("</tbody></table>");
        }

        html.Append("</section>");

        // ---- Latency ----
        html.Append("<section><h2>").Append(E(loc.Get("report.p.latency"))).Append("</h2>")
            .Append("<p class=\"note\">").Append(E(loc.Get("report.p.latency.note"))).Append("</p>");
        if (data.LatencyByDay.Count == 0)
        {
            html.Append("<p>").Append(E(loc.Get("report.p.noData"))).Append("</p>");
        }
        else
        {
            html.Append(LatencyChart(data.LatencyByDay, loc));
        }

        html.Append("</section>");

        // ---- Speed tests ----
        html.Append("<section><h2>").Append(E(loc.Get("report.p.speeds"))).Append("</h2>");
        html.Append("<p class=\"note\">").Append(E(options.SubscribedDownloadMbps > 0
            ? loc.Format("report.p.subscription", Number(options.SubscribedDownloadMbps), Number(options.SubscribedUploadMbps))
            : loc.Get("report.p.noSubscription"))).Append("</p>");
        if (data.SpeedTests.Count == 0)
        {
            html.Append("<p>").Append(E(loc.Get("report.p.noSpeedTests"))).Append("</p>");
        }
        else
        {
            if (options.SubscribedDownloadMbps > 0)
            {
                html.Append(SpeedChart(data.SpeedTests, options.SubscribedDownloadMbps, loc));
            }

            html.Append("<table><thead><tr>")
                .Append(Th(loc.Get("report.p.col.when"))).Append(Th(loc.Get("report.p.col.download"), true))
                .Append(Th(loc.Get("report.p.col.upload"), true)).Append(Th(loc.Get("report.p.col.ping"), true))
                .Append(Th(loc.Get("report.p.col.loaded"), true)).Append("</tr></thead><tbody>");
            foreach (SpeedTestEntry test in data.SpeedTests)
            {
                bool below = options.SubscribedDownloadMbps > 0 && test.DownloadMbps < options.SubscribedDownloadMbps * BelowPlanThreshold;
                string download = Number(test.DownloadMbps) + " Mbit/s"
                    + (options.SubscribedDownloadMbps > 0 ? " (" + Percent(test.DownloadMbps / options.SubscribedDownloadMbps, 0) + ")" : string.Empty);
                string upload = Number(test.UploadMbps) + " Mbit/s"
                    + (options.SubscribedUploadMbps > 0 ? " (" + Percent(test.UploadMbps / options.SubscribedUploadMbps, 0) + ")" : string.Empty);
                string loaded = test.BufferbloatMs is { } extra
                    ? $"+{extra:F0} ms ({Tools.SpeedTest.SpeedTestResult.Grade(extra)})"
                    : "-";
                html.Append(below ? "<tr class=\"provider\">" : "<tr>")
                    .Append(Td(Local(test.AtUtc, "dd-MM-yyyy HH:mm"))).Append(Td(download, true)).Append(Td(upload, true))
                    .Append(Td(test.PingMs is { } ping ? $"{ping:F0} ms" : "-", true)).Append(Td(loaded, true))
                    .Append("</tr>");
            }

            html.Append("</tbody></table><p class=\"note\">").Append(E(loc.Get("report.p.loaded.note"))).Append("</p>");
        }

        html.Append("</section>");

        // ---- Method ----
        html.Append("<section><h2>").Append(E(loc.Get("report.p.method"))).Append("</h2>")
            .Append("<p>").Append(E(loc.Get("report.p.method.text"))).Append("</p>")
            .Append("<p>").Append(E(loc.Get("report.p.method.sides"))).Append("</p></section>");

        html.Append("<footer>").Append(E(loc.Format("report.p.footer", options.AppVersion))).Append("</footer></main></body></html>");
        return html.ToString();

        string Th(string text, bool numeric = false) => (numeric ? "<th class=\"num\">" : "<th>") + E(text) + "</th>";
        string Td(string text, bool numeric = false) => (numeric ? "<td class=\"num\">" : "<td>") + E(text) + "</td>";
    }

    private static void Tile(StringBuilder html, string label, string value, string? note)
    {
        html.Append("<div class=\"tile\"><div class=\"label\">").Append(WebUtility.HtmlEncode(label)).Append("</div>")
            .Append("<div class=\"value\">").Append(WebUtility.HtmlEncode(value)).Append("</div>");
        if (note is not null)
        {
            html.Append("<div class=\"tnote\">").Append(WebUtility.HtmlEncode(note)).Append("</div>");
        }

        html.Append("</div>");
    }

    private static string SideLabel(OutageSide side, LocalizationManager loc) => side switch
    {
        OutageSide.ThisComputer => loc.Get("report.p.side.computer"),
        OutageSide.HomeNetwork => loc.Get("report.p.side.home"),
        OutageSide.Provider => loc.Get("report.p.side.provider"),
        _ => loc.Get("report.p.side.other"),
    };

    public static string Duration(TimeSpan span, LocalizationManager loc)
    {
        if (span.TotalDays >= 1)
        {
            return loc.Format("report.p.dur.dh", (int)span.TotalDays, span.Hours);
        }

        if (span.TotalHours >= 1)
        {
            return loc.Format("report.p.dur.hm", (int)span.TotalHours, span.Minutes);
        }

        return span.TotalMinutes >= 1
            ? loc.Format("report.p.dur.ms", (int)span.TotalMinutes, span.Seconds)
            : loc.Format("report.p.dur.s", Math.Max(0, (int)Math.Round(span.TotalSeconds)));
    }

    private static string Percent(double fraction, int decimals) =>
        (fraction * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";

    private static string Number(double value) =>
        value.ToString(value >= 100 ? "F0" : "F1", CultureInfo.InvariantCulture);

    // ---- Charts ---------------------------------------------------------------------------------

    private const int ChartWidth = 720;
    private const int ChartHeight = 220;
    private const int PadLeft = 78;
    private const int PadBottom = 34;
    private const int PadTop = 12;
    private const int PadRight = 24;

    private static string LatencyChart(IReadOnlyList<DayLatency> days, LocalizationManager loc)
    {
        double max = NiceMax(days.Max(d => d.P95Ms));
        int plotWidth = ChartWidth - PadLeft - PadRight;
        int plotHeight = ChartHeight - PadTop - PadBottom;
        double X(int i) => PadLeft + (days.Count == 1 ? plotWidth / 2.0 : i * plotWidth / (double)(days.Count - 1));
        double Y(double v) => PadTop + plotHeight - (v / max * plotHeight);

        var svg = new StringBuilder();
        Axes(svg, max, "ms");

        string Line(Func<DayLatency, double> value) =>
            string.Join(" ", days.Select((d, i) => Invariant($"{X(i):F1},{Y(value(d)):F1}")));

        svg.Append(Invariant($"<polyline class=\"p95\" points=\"{Line(d => d.P95Ms)}\"/>"))
           .Append(Invariant($"<polyline class=\"avg\" points=\"{Line(d => d.AverageMs)}\"/>"));

        int step = Math.Max(1, (int)Math.Ceiling(days.Count / 10.0));
        for (int i = 0; i < days.Count; i += step)
        {
            string anchor = days.Count > 1 && i == days.Count - 1 ? "end" : "middle";
            svg.Append(Invariant($"<text class=\"axis\" x=\"{X(i):F1}\" y=\"{ChartHeight - 12}\" text-anchor=\"{anchor}\">{days[i].Day:dd-MM}</text>"));
        }

        return Svg(svg.ToString(), loc.Get("report.p.latency")) + Legend(
            ("avg", loc.Get("report.p.legend.avg")),
            ("p95", loc.Get("report.p.legend.p95")));
    }

    private static string SpeedChart(IReadOnlyList<SpeedTestEntry> tests, double subscribed, LocalizationManager loc)
    {
        double max = NiceMax(Math.Max(subscribed, tests.Max(t => t.DownloadMbps)));
        int plotWidth = ChartWidth - PadLeft - PadRight;
        int plotHeight = ChartHeight - PadTop - PadBottom;
        double slot = plotWidth / (double)tests.Count;
        double barWidth = Math.Max(2, Math.Min(28, slot * 0.6));
        double Y(double v) => PadTop + plotHeight - (v / max * plotHeight);

        var svg = new StringBuilder();
        Axes(svg, max, "Mbit/s");
        for (int i = 0; i < tests.Count; i++)
        {
            double x = PadLeft + (i * slot) + ((slot - barWidth) / 2);
            double y = Y(tests[i].DownloadMbps);
            string css = tests[i].DownloadMbps < subscribed * BelowPlanThreshold ? "bar low" : "bar";
            svg.Append(Invariant($"<rect class=\"{css}\" x=\"{x:F1}\" y=\"{y:F1}\" width=\"{barWidth:F1}\" height=\"{PadTop + plotHeight - y:F1}\"/>"));
        }

        svg.Append(Invariant($"<line class=\"plan\" x1=\"{PadLeft}\" x2=\"{ChartWidth - PadRight}\" y1=\"{Y(subscribed):F1}\" y2=\"{Y(subscribed):F1}\"/>"));
        svg.Append(Invariant($"<line class=\"plan80\" x1=\"{PadLeft}\" x2=\"{ChartWidth - PadRight}\" y1=\"{Y(subscribed * BelowPlanThreshold):F1}\" y2=\"{Y(subscribed * BelowPlanThreshold):F1}\"/>"));

        return Svg(svg.ToString(), loc.Get("report.p.speeds")) + Legend(
            ("bar", loc.Get("report.p.legend.download")),
            ("plan", loc.Get("report.p.legend.plan")),
            ("plan80", loc.Get("report.p.legend.plan80")));
    }

    private static void Axes(StringBuilder svg, double max, string unit)
    {
        int plotHeight = ChartHeight - PadTop - PadBottom;
        for (int i = 0; i <= 4; i++)
        {
            double value = max * i / 4;
            double y = PadTop + plotHeight - (value / max * plotHeight);
            svg.Append(Invariant($"<line class=\"grid\" x1=\"{PadLeft}\" x2=\"{ChartWidth - PadRight}\" y1=\"{y:F1}\" y2=\"{y:F1}\"/>"))
               .Append(Invariant($"<text class=\"axis\" x=\"{PadLeft - 6}\" y=\"{y + 4:F1}\" text-anchor=\"end\">{value.ToString("0.#", CultureInfo.InvariantCulture)} {unit}</text>"));
        }
    }

    private static string Svg(string body, string title) =>
        Invariant($"<svg viewBox=\"0 0 {ChartWidth} {ChartHeight}\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(title)}\">{body}</svg>");

    private static string Legend(params (string Css, string Label)[] items) =>
        "<p class=\"legend\">" + string.Join(" ", items.Select(i => $"<span class=\"key {i.Css}\"></span>{WebUtility.HtmlEncode(i.Label)}")) + "</p>";

    /// <summary>SVG numbers need a dot as decimal separator whatever the machine's culture.</summary>
    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    private static double NiceMax(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double normalized = value / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 2.5 ? 2.5 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private const string Css = """
        :root { --text:#1b2530; --muted:#5d6b7a; --line:#d9e0e8; --accent:#276ba4; --warn:#a86a06; --bad:#c0322c; --soft:#f4f7fa; }
        * { box-sizing:border-box; }
        body { margin:0; background:#fff; color:var(--text); font:15px/1.5 "Segoe UI", system-ui, -apple-system, sans-serif; }
        main { max-width:860px; margin:0 auto; padding:32px 24px 48px; }
        h1 { font-size:26px; margin:0 0 4px; }
        h2 { font-size:19px; margin:32px 0 10px; padding-bottom:6px; border-bottom:2px solid var(--line); }
        h3 { font-size:15px; margin:18px 0 6px; }
        .meta, .note, .legend, footer, .tnote { color:var(--muted); font-size:13px; }
        .tiles { display:grid; grid-template-columns:repeat(auto-fit, minmax(150px, 1fr)); gap:10px; }
        .tile { background:var(--soft); border:1px solid var(--line); border-radius:8px; padding:10px 12px; }
        .tile .label { font-size:12px; color:var(--muted); }
        .tile .value { font-size:22px; font-weight:600; }
        table { width:100%; border-collapse:collapse; font-size:13.5px; }
        th, td { text-align:left; padding:5px 8px; border-bottom:1px solid var(--line); }
        th { color:var(--muted); font-weight:600; }
        .num { text-align:right; white-space:nowrap; }
        tr.provider td { background:#fdf3f2; }
        svg { width:100%; height:auto; display:block; margin:8px 0 2px; }
        svg .grid { stroke:var(--line); stroke-width:1; }
        svg .axis { fill:var(--muted); font-size:11px; }
        svg .avg { fill:none; stroke:var(--accent); stroke-width:2.2; }
        svg .p95 { fill:none; stroke:var(--warn); stroke-width:1.6; stroke-dasharray:5 4; }
        svg .bar { fill:var(--accent); }
        svg .bar.low { fill:var(--bad); }
        svg .plan { stroke:var(--text); stroke-width:1.5; }
        svg .plan80 { stroke:var(--bad); stroke-width:1.2; stroke-dasharray:4 4; }
        .legend .key { display:inline-block; width:18px; height:3px; vertical-align:middle; margin:0 6px 0 14px; }
        .legend .key:first-child { margin-left:0; }
        .legend .avg, .legend .bar { background:var(--accent); }
        .legend .p95 { background:var(--warn); }
        .legend .plan { background:var(--text); }
        .legend .plan80 { background:var(--bad); }
        footer { margin-top:40px; padding-top:10px; border-top:1px solid var(--line); }
        @media print { main { padding:0; } h2 { break-after:avoid; } table, svg, .tiles { break-inside:avoid; } }
        """;
}
