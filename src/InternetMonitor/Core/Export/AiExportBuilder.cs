using System.Globalization;
using System.Text;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.History;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;
using InternetMonitor.Core.Steps;
using InternetMonitor.Core.Tools.SpeedTest;
using InternetMonitor.Localization;

namespace InternetMonitor.Core.Export;

/// <summary>
/// Builds the "export for AI analysis" markdown document, and its plain twin without the
/// instructions - as much of what the monitor recorded as can usefully be read in one document.
///
/// It is a superset of the provider report (coverage, availability, outages by cause and by side,
/// latency per day, every speed test against the subscription) plus what only an analyst needs:
/// every incident with its timeline, what changed just before it, and every step's measurements at
/// its start; when in the day and week problems happen; Wi-Fi signal and access-point switches;
/// this computer's own traffic; and summary statistics for every recorded series. Raw per-sample
/// data stays in the "export all data" zip, which an AI can be given as well.
///
/// English throughout, whatever the app's language: the reader is an AI, and incident text is
/// recorded in English anyway. Network names, access points and MAC addresses are replaced by
/// stable stand-ins (see <see cref="Pseudonymizer"/>) so the document can be pasted into any AI
/// tool. A pure function over data the caller already has.
/// </summary>
public static class AiExportBuilder
{
    /// <summary>How far back the summary sections look - the same window history is kept for.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    private const string Instructions = """
        ## Instructions for the AI

        You are a network-diagnostics specialist. Below is data exported from Internet Monitor, a
        Windows tool that checks the internet connection of one computer every few seconds and
        records what it finds. Your reader is the person who owns this computer; assume no network
        expertise when you phrase conclusions, but be precise about the evidence.

        Work through the data and produce, in this order:

        1. **Summary** - three to five sentences: how healthy is this connection over the period,
           and what is the single most important finding?
        2. **Findings** - each with the evidence it rests on (incident IDs, dates and times, numbers
           from the tables) and a confidence level (high / medium / low). Look in particular for:
           - recurring outages: the same cause, the same time of day or day of week;
           - where problems originate - this computer, the home network/router, or the provider
             (see "How to read this data" for how each cause maps to a side);
           - Wi-Fi: weak signal, access-point switches, or signal drops shortly before incidents;
           - latency that rises with this computer's own traffic or under speed-test load
             (bufferbloat in the router) versus latency that rises on an idle line (provider);
           - speed tests that fall short of the subscription, and whether that is consistent or
             tied to particular times;
           - changes recorded just before incidents (address, gateway, DNS servers, adapter,
             Wi-Fi network or access point).
        3. **Likely root causes**, ranked, each with what would confirm or rule it out.
        4. **Recommendations** - concrete steps, starting with the cheapest and most likely to help.
           Say who should act: the user (settings, placement, cabling, router configuration) or
           the provider. If something is worth raising with the provider, say what evidence from
           this data to give them.
        5. **What to measure next** if the data is not conclusive.

        Ground every claim in the data. Where the data cannot answer a question, say so rather than
        speculating. Mind the coverage figure: time the app was not running is simply absent, not
        "up" or "down". Times are given in UTC and in the computer's local time zone.
        """;

    private const string HowToRead = """
        ## How to read this data

        **The checks.** Every cycle (every few seconds while a window is open, every 15 seconds in
        the background by default) seven steps run, working outwards: (1) network adapter,
        (2) cable or Wi-Fi link, (3) IP address, (4) gateway/router - ICMP raced against TCP,
        (5) public internet - ICMP pings to public addresses, first answer wins, (6) DNS - the
        adapter's own DNS servers, then public resolvers as a cross-check, (7) target - an HTTPS
        reachability check plus any endpoints the user configured, with per-stage timing.
        All steps use the same adapter. History is sampled at most every 15 seconds.

        **Incidents.** An incident opens after two consecutive failing cycles and closes after two
        consecutive good ones, so very short blips do not appear. Each records its most likely
        cause, a timeline, every step's result at the start and at resolution, and what changed in
        the environment in the 30 minutes before it opened.

        **Causes and where they originate.**
        - This computer: Adapter, Link (cable or Wi-Fi), IpConfiguration.
        - Home network / router: Gateway (the router did not answer - this may be the provider's
          own equipment).
        - Provider: Internet (router fine, nothing beyond it answered), Dns (the DNS server failed),
          Target (the HTTPS reachability check failed while the steps before it worked).
        - Other: CaptivePortal (a login page on hotel/train/guest Wi-Fi), Unknown.
        - Not outages: FirewallSuspected (pings filtered while real traffic works) and
          NetworkHealth (packet-level warnings: retransmissions, loss, TTL errors, rogue DHCP).

        **Series units.** Latency series in milliseconds; adapter/link are 1 (up) or 0 (down);
        Wi-Fi signal is Windows' signal quality in percent; traffic and speed tests in Mbit/s;
        "extra ping under load" is how many milliseconds the round trip grew while a speed test
        filled the line (A under 5 ms, B under 30, C under 60, D under 200, F 200 or more).

        **Language.** Incident headlines and explanations are recorded in English. Measurement
        labels and values were recorded in the app's display language at the time; fixed terms
        have been translated back to English here, composed values may still be in that language.

        **Limits.** Only this computer is measured: traffic from other devices on the same line is
        invisible, and a problem with only another device's Wi-Fi does not show. ICMP being filtered
        somewhere is common and is not itself an outage.
        """;

    public static string Build(
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries,
        AppSettings settings,
        string appVersion = "",
        TimeZoneInfo? timeZone = null,
        DateTimeOffset? now = null) =>
        Compose("# Internet Monitor - Data Export for AI Analysis", Instructions, incidents, historySeries, settings, appVersion, timeZone, now);

    /// <summary>
    /// The same document without the instructions for an AI - for a user who wants the data
    /// itself. The "how to read" section stays: it is what makes the numbers interpretable.
    /// </summary>
    public static string BuildPlain(
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> historySeries,
        AppSettings settings,
        string appVersion = "",
        TimeZoneInfo? timeZone = null,
        DateTimeOffset? now = null) =>
        Compose("# Internet Monitor - Data Export", null, incidents, historySeries, settings, appVersion, timeZone, now);

    private static string Compose(
        string title,
        string? instructions,
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> history,
        AppSettings settings,
        string appVersion,
        TimeZoneInfo? timeZone,
        DateTimeOffset? now)
    {
        TimeZoneInfo zone = timeZone ?? TimeZoneInfo.Local;
        DateTimeOffset to = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset from = to - Window;
        IReadOnlyList<MetricPoint> Series(string id) => history.TryGetValue(id, out IReadOnlyList<MetricPoint>? points) ? points : [];

        var names = new Pseudonymizer();
        names.Learn(incidents, Series(SeriesNames.WifiSignal));

        ProviderReportData report = ProviderReportData.Compute(incidents, Series, from, to, zone);
        var doc = new Doc(zone, names);

        doc.Line(title).Blank();
        if (instructions is not null)
        {
            doc.Line(instructions).Blank();
        }

        AppendContext(doc, settings, appVersion, from, to);
        doc.Line(HowToRead).Blank();
        AppendSummary(doc, report, settings);
        AppendOutagePatterns(doc, report);
        AppendIncidents(doc, incidents);
        AppendLatency(doc, Series, from, to);
        AppendSpeedTests(doc, report, settings);
        AppendWifi(doc, Series(SeriesNames.WifiSignal), from, to);
        AppendTraffic(doc, Series(SeriesNames.TrafficDown), Series(SeriesNames.TrafficUp), report, from, to);
        AppendSeriesStatistics(doc, history);
        AppendNetworkHealth(doc, incidents, settings);

        return doc.ToString();
    }

    // ---- Sections -------------------------------------------------------------------------------

    private static void AppendContext(Doc doc, AppSettings settings, string appVersion, DateTimeOffset from, DateTimeOffset to)
    {
        doc.Line("## About this export").Blank()
            .Item("Generated", doc.Both(to))
            .Item("Summary period", $"{doc.Both(from)} to {doc.Both(to)} (last {Window.TotalDays:F0} days)")
            .Item("Computer's time zone", $"{doc.Zone.Id} (UTC{doc.Zone.GetUtcOffset(to):hh\\:mm} now)");
        if (appVersion.Length > 0)
        {
            doc.Item("App version", appVersion);
        }

        var targets = new CheckTargets();
        doc.Item("Check interval", I($"{settings.ForegroundIntervalSeconds} s with a window open, {settings.BackgroundIntervalSeconds} s in the background"))
            .Item("Latency thresholds", I($"warning at {targets.LatencyWarningMs} ms, severe at {targets.LatencyErrorMs} ms"))
            .Item("Weak Wi-Fi threshold", I($"below {targets.WeakWifiSignalPercent}% signal"))
            .Item("Public ping addresses (step 5)", string.Join(", ", settings.ParsedPublicIpTargets()))
            .Item("User's own last-resort DNS servers", settings.ParsedCustomDnsServers() is { Count: > 0 } custom ? string.Join(", ", custom) : "none")
            .Item("User-configured endpoints (step 7)", settings.ApplicationEndpoints.Count(e => e.Enabled) is var n and > 0
                ? I($"{n}: ") + string.Join(", ", settings.ApplicationEndpoints.Where(e => e.Enabled).Select(e => e.Name))
                : "none")
            .Item("Subscription speed", settings.SubscribedDownloadMbps > 0
                ? I($"{settings.SubscribedDownloadMbps} Mbit/s down, {settings.SubscribedUploadMbps} Mbit/s up")
                : "not entered")
            .Item("Automatic speed test", settings.SpeedTestAutoRunEnabled ? I($"every {settings.SpeedTestIntervalHours} h") : "off")
            .Item("Network-health monitoring", settings.NetworkHealthMonitoringEnabled ? "on" : "off")
            .Blank();
    }

    private static void AppendSummary(Doc doc, ProviderReportData report, AppSettings settings)
    {
        doc.Line("## Summary of the period").Blank()
            .Item("Measured", I($"{Pct(report.Coverage, 1)} of the period ({Duration(report.Monitored)}); the rest the app was not running"))
            .Item("Availability (provider-side outages only)", report.Availability is { } a ? I($"{Pct(a, 3)}") : "n/a - nothing measured");

        foreach (IGrouping<OutageSide, OutageEntry> side in report.Outages.GroupBy(o => o.Side).OrderBy(g => g.Key))
        {
            TimeSpan total = side.Aggregate(TimeSpan.Zero, (sum, o) => sum + o.Duration);
            doc.Item(
                I($"Outages - {SideName(side.Key)}"),
                I($"{side.Count()}, {Duration(total)} in total, longest {Duration(side.Max(o => o.Duration))}"));
        }

        if (report.Outages.Count == 0)
        {
            doc.Item("Outages", "none recorded");
        }

        if (settings.SubscribedDownloadMbps > 0 && report.SpeedTests.Count > 0)
        {
            int below = report.SpeedTests.Count(t => t.DownloadMbps < settings.SubscribedDownloadMbps * 0.8);
            doc.Item("Speed tests below 80% of subscribed download", I($"{below} of {report.SpeedTests.Count}"));
        }

        doc.Blank();

        if (report.OutagesByCause.Count > 0)
        {
            doc.Line("### Outages by cause").Blank()
                .Row("Cause", "Side", "Count", "Total", "Longest").Rule(5);
            foreach (OutageGroup group in report.OutagesByCause)
            {
                doc.Row(group.Cause.ToString(), SideName(group.Side), I($"{group.Count}"), Duration(group.Total), Duration(group.Longest));
            }

            doc.Blank();
        }
    }

    private static void AppendOutagePatterns(Doc doc, ProviderReportData report)
    {
        if (report.Outages.Count == 0)
        {
            return;
        }

        doc.Line("### When outages start (local time)").Blank();

        var byHour = report.Outages
            .GroupBy(o => doc.Local(o.StartUtc).Hour)
            .OrderBy(g => g.Key)
            .Select(g => I($"{g.Key:00}h: {g.Count()}"));
        doc.Item("By hour of day", string.Join(", ", byHour));

        var byDay = report.Outages
            .GroupBy(o => doc.Local(o.StartUtc).DayOfWeek)
            .OrderBy(g => ((int)g.Key + 6) % 7)
            .Select(g => I($"{g.Key}: {g.Count()}"));
        doc.Item("By day of week", string.Join(", ", byDay)).Blank();
    }

    private static void AppendIncidents(Doc doc, IReadOnlyList<Incident> incidents)
    {
        doc.Line("## Incidents (all recorded, newest first)").Blank();
        if (incidents.Count == 0)
        {
            doc.Line("No incidents recorded.").Blank();
            return;
        }

        foreach (Incident incident in incidents.OrderByDescending(i => i.StartUtc))
        {
            string side = incident.ConditionKey is null ? SideName(ProviderReportData.SideOf(incident.Cause)) : "packet-level warning";
            doc.Line(I($"### {incident.Id} - {doc.Names.Text(incident.Headline)}")).Blank()
                .Item("Cause", incident.ConditionKey is { } condition ? I($"{incident.Cause} ({condition})") : incident.Cause.ToString())
                .Item("Side", side)
                .Item("Start", doc.Both(incident.StartUtc))
                .Item("End", incident.EndUtc is { } end ? doc.Both(end) : "still active")
                .Item("Duration", incident.Duration is { } d ? Duration(d) : "ongoing");

            if (incident.AffectedSteps.Count > 0)
            {
                doc.Item("Affected steps", string.Join(", ", incident.AffectedSteps));
            }

            if (incident.Explanation is { } explanation)
            {
                doc.Item("What happened", doc.Names.Text(explanation.What))
                    .Item("Likely cause", doc.Names.Text(explanation.LikelyCause))
                    .Item("What to do", doc.Names.Text(explanation.WhatToDo));
            }

            if (incident.RecentChanges.Count > 0)
            {
                doc.Line("- Changed in the 30 minutes before (local time):");
                foreach (string change in incident.RecentChanges)
                {
                    doc.Line("  - " + doc.Names.Text(change));
                }
            }

            if (incident.Timeline.Count > 0)
            {
                doc.Line("- Timeline:");
                foreach (TimelineEntry entry in incident.Timeline)
                {
                    doc.Line(I($"  - {doc.LocalTime(entry.AtUtc)} - ") + doc.Names.Text(entry.Text));
                }
            }

            if (incident.StepsAtStart.Count > 0)
            {
                doc.Line("- Steps when it started:");
                foreach (StepSnapshotEntry step in incident.StepsAtStart)
                {
                    string detail = step.Detail is { Length: > 0 } text ? " - " + doc.Names.Text(text) : string.Empty;
                    doc.Line(I($"  - {step.Step}: {step.State} - ") + doc.Names.Text(step.Headline) + detail);
                    foreach ((string label, string value) in step.Measurements)
                    {
                        string english = LocalizationManager.ToEnglish(value);
                        doc.Line(I($"    - {LocalizationManager.ToEnglish(label)}: ") + doc.Names.Measurement(label, english));
                    }
                }
            }

            if (incident.StepsAtResolution is { Count: > 0 } resolved)
            {
                doc.Line("- Steps when it resolved: " + string.Join("; ", resolved.Select(s => I($"{s.Step} {s.State}"))));
            }

            doc.Blank();
        }
    }

    private static void AppendLatency(Doc doc, Func<string, IReadOnlyList<MetricPoint>> series, DateTimeOffset from, DateTimeOffset to)
    {
        (string Name, string Id)[] latencySeries =
        [
            ("Gateway", SeriesNames.For(StepId.Gateway)!),
            ("Public internet", SeriesNames.For(StepId.PublicIp)!),
            ("DNS", SeriesNames.For(StepId.Dns)!),
            ("Target (HTTPS)", SeriesNames.For(StepId.Target)!),
        ];

        Dictionary<string, List<MetricPoint>> inPeriod = latencySeries.ToDictionary(
            s => s.Id,
            s => series(s.Id).Where(p => p.Timestamp >= from && p.Timestamp <= to).ToList());
        if (inPeriod.Values.All(p => p.Count == 0))
        {
            return;
        }

        doc.Line("## Latency per day (ms, local dates)").Blank();
        doc.Line("Average / 95th percentile per series; n = samples of the public-internet series.").Blank();
        doc.Row(["Date", .. latencySeries.Select(s => s.Name), "n"]).Rule(latencySeries.Length + 2);

        IEnumerable<DateOnly> days = inPeriod.Values.SelectMany(p => p).Select(p => DateOnly.FromDateTime(doc.Local(p.Timestamp))).Distinct().Order();
        foreach (DateOnly day in days)
        {
            var cells = new List<string> { day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            foreach ((_, string id) in latencySeries)
            {
                double[] values = [.. inPeriod[id].Where(p => DateOnly.FromDateTime(doc.Local(p.Timestamp)) == day).Select(p => p.Value).Order()];
                cells.Add(values.Length == 0 ? "-" : I($"{values.Average():F0} / {Percentile(values, 0.95):F0}"));
            }

            cells.Add(I($"{inPeriod[latencySeries[1].Id].Count(p => DateOnly.FromDateTime(doc.Local(p.Timestamp)) == day)}"));
            doc.Row([.. cells]);
        }

        doc.Blank();

        List<MetricPoint> internet = inPeriod[latencySeries[1].Id];
        if (internet.Count > 0)
        {
            doc.Line("### Public-internet latency by hour of day (local, all days combined)").Blank()
                .Row("Hour", "Average ms", "P95 ms", "Samples").Rule(4);
            foreach (IGrouping<int, MetricPoint> hour in internet.GroupBy(p => doc.Local(p.Timestamp).Hour).OrderBy(g => g.Key))
            {
                double[] values = [.. hour.Select(p => p.Value).Order()];
                doc.Row(I($"{hour.Key:00}"), I($"{values.Average():F1}"), I($"{Percentile(values, 0.95):F1}"), I($"{values.Length}"));
            }

            doc.Blank();
        }
    }

    private static void AppendSpeedTests(Doc doc, ProviderReportData report, AppSettings settings)
    {
        doc.Line("## Speed tests").Blank();
        if (report.SpeedTests.Count == 0)
        {
            doc.Line("No speed tests in the period.").Blank();
            return;
        }

        doc.Row("When (local)", "Download Mbit/s", "Upload Mbit/s", "% of subscribed down", "Ping ms", "Extra ping under load").Rule(6);
        foreach (SpeedTestEntry test in report.SpeedTests)
        {
            doc.Row(
                doc.LocalTime(test.AtUtc),
                I($"{test.DownloadMbps:F1}"),
                I($"{test.UploadMbps:F1}"),
                settings.SubscribedDownloadMbps > 0 ? I($"{Pct(test.DownloadMbps / settings.SubscribedDownloadMbps, 0)}") : "-",
                test.PingMs is { } ping ? I($"{ping:F0}") : "-",
                test.BufferbloatMs is { } extra ? I($"+{extra:F0} ms ({SpeedTestResult.Grade(extra)})") : "-");
        }

        doc.Blank();
    }

    private static void AppendWifi(Doc doc, IReadOnlyList<MetricPoint> wifi, DateTimeOffset from, DateTimeOffset to)
    {
        List<MetricPoint> points = [.. wifi.Where(p => p.Timestamp >= from && p.Timestamp <= to)];
        if (points.Count == 0)
        {
            return;
        }

        double[] values = [.. points.Select(p => p.Value).Order()];
        doc.Line("## Wi-Fi").Blank()
            .Item("Signal (Windows quality %)", I($"min {values[0]:F0}, average {values.Average():F0}, p5 {Percentile(values, 0.05):F0}, max {values[^1]:F0} over {values.Length} samples"))
            .Item("Time below 40% / 40-60% / above 60%", I($"{Pct(Share(values, v => v < 40), 1)} / {Pct(Share(values, v => v is >= 40 and <= 60), 1)} / {Pct(Share(values, v => v > 60), 1)}"));

        var switches = new List<string>();
        string? previous = null;
        foreach (MetricPoint point in points)
        {
            if (WifiHistory.AccessPointOf(point.Metadata) is not { } ap)
            {
                continue;
            }

            string alias = doc.Names.AccessPoint(ap);
            if (previous is not null && alias != previous)
            {
                switches.Add(I($"{doc.LocalTime(point.Timestamp)}: {previous} -> {alias} (signal {point.Value:F0}%)"));
            }

            previous = alias;
        }

        doc.Item("Access-point switches", I($"{switches.Count}"));
        foreach (string change in switches.TakeLast(50))
        {
            doc.Line("  - " + change);
        }

        doc.Blank().Line("### Wi-Fi signal per day").Blank().Row("Date", "Average %", "Minimum %", "Samples").Rule(4);
        foreach (IGrouping<DateOnly, MetricPoint> day in points.GroupBy(p => DateOnly.FromDateTime(doc.Local(p.Timestamp))).OrderBy(g => g.Key))
        {
            doc.Row(day.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), I($"{day.Average(p => p.Value):F0}"), I($"{day.Min(p => p.Value):F0}"), I($"{day.Count()}"));
        }

        doc.Blank();
    }

    private static void AppendTraffic(Doc doc, IReadOnlyList<MetricPoint> down, IReadOnlyList<MetricPoint> up, ProviderReportData report, DateTimeOffset from, DateTimeOffset to)
    {
        double[] d = [.. down.Where(p => p.Timestamp >= from && p.Timestamp <= to).Select(p => p.Value).Order()];
        double[] u = [.. up.Where(p => p.Timestamp >= from && p.Timestamp <= to).Select(p => p.Value).Order()];
        if (d.Length == 0)
        {
            return;
        }

        doc.Line("## This computer's own traffic (Mbit/s per cycle)").Blank()
            .Item("Download", I($"average {d.Average():F1}, p95 {Percentile(d, 0.95):F1}, max {d[^1]:F1}"))
            .Item("Upload", u.Length > 0 ? I($"average {u.Average():F1}, p95 {Percentile(u, 0.95):F1}, max {u[^1]:F1}") : "-");

        if (report.SpeedTests.LastOrDefault() is { DownloadMbps: >= 1 } latest)
        {
            doc.Item("Samples above 70% of the last measured download speed", I($"{Pct(Share(d, v => v >= latest.DownloadMbps * 0.7), 1)}"));
        }

        doc.Blank();
    }

    private static void AppendSeriesStatistics(Doc doc, IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> history)
    {
        doc.Line("## All recorded series (entire history kept)").Blank();
        if (history.All(s => s.Value.Count == 0))
        {
            doc.Line("No history recorded.").Blank();
            return;
        }

        doc.Row("Series", "Unit", "Points", "First (local)", "Last (local)", "Min", "Avg", "P95", "Max").Rule(9);
        foreach ((string id, IReadOnlyList<MetricPoint> points) in history.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (points.Count == 0)
            {
                continue;
            }

            double[] values = [.. points.Select(p => p.Value).Order()];
            ChartableSeries? known = SeriesNames.Chartable.FirstOrDefault(s => s.Id == id);
            doc.Row(
                FriendlyName(id),
                known is null ? LegacyUnit(id) : known.IsState ? "1 = up, 0 = down" : known.Unit,
                I($"{values.Length}"),
                doc.LocalTime(points[0].Timestamp),
                doc.LocalTime(points[^1].Timestamp),
                I($"{values[0]:F1}"),
                I($"{values.Average():F1}"),
                I($"{Percentile(values, 0.95):F1}"),
                I($"{values[^1]:F1}"));
        }

        doc.Blank();
    }

    private static void AppendNetworkHealth(Doc doc, IReadOnlyList<Incident> incidents, AppSettings settings)
    {
        if (!settings.NetworkHealthMonitoringEnabled && !incidents.Any(i => i.ConditionKey is not null))
        {
            return;
        }

        doc.Line("## Network-health warnings").Blank();
        var grouped = incidents.Where(i => i.ConditionKey is { Length: > 0 }).GroupBy(i => i.ConditionKey!).OrderByDescending(g => g.Count()).ToList();
        if (grouped.Count == 0)
        {
            doc.Line("None recorded.").Blank();
            return;
        }

        foreach (IGrouping<string, Incident> group in grouped)
        {
            doc.Item(group.Key, I($"{group.Count()} time(s), most recently {doc.LocalTime(group.Max(i => i.StartUtc))}"));
        }

        doc.Blank();
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private static string I(FormattableString text) => FormattableString.Invariant(text);

    private static string Pct(double fraction, int decimals) =>
        (fraction * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";

    private static double Share(double[] values, Func<double, bool> predicate) =>
        values.Length == 0 ? 0 : values.Count(predicate) / (double)values.Length;

    /// <summary>Nearest-rank percentile over an already-ascending array.</summary>
    private static double Percentile(double[] sortedAscending, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sortedAscending.Length) - 1;
        return sortedAscending[Math.Clamp(rank, 0, sortedAscending.Length - 1)];
    }

    private static string Duration(TimeSpan duration) => duration.TotalHours >= 1
        ? I($"{duration.TotalHours:F1} h")
        : duration.TotalMinutes >= 1
            ? I($"{duration.TotalMinutes:F1} min")
            : I($"{duration.TotalSeconds:F0} s");

    private static string SideName(OutageSide side) => side switch
    {
        OutageSide.ThisComputer => "this computer",
        OutageSide.HomeNetwork => "home network / router",
        OutageSide.Provider => "provider",
        _ => "other",
    };

    /// <summary>English names for the series ids - several are v1's probe ids, kept for on-disk continuity (see <see cref="SeriesNames"/>).</summary>
    private static string FriendlyName(string seriesId) =>
        SeriesNames.Chartable.FirstOrDefault(s => s.Id == seriesId) is { } series
            ? LocalizationManager.GetEnglish(series.NameKey)
            : seriesId switch
            {
                "ip" => "IP address (v1)",
                "time" => "Time sync (v1)",
                _ when seriesId.StartsWith("internet:", StringComparison.Ordinal) => $"Ping to {seriesId["internet:".Length..]} (v1)",
                _ when seriesId.StartsWith("endpoint:", StringComparison.Ordinal) => $"Configured endpoint {seriesId["endpoint:".Length..][..Math.Min(6, seriesId.Length - "endpoint:".Length)]} (v1)",
                _ => seriesId,
            };

    private static string LegacyUnit(string seriesId) => seriesId switch
    {
        _ when seriesId.StartsWith("internet:", StringComparison.Ordinal) || seriesId.StartsWith("endpoint:", StringComparison.Ordinal) => "ms",
        "time" => "s (clock offset)",
        _ => "-",
    };

    /// <summary>A small markdown writer that also knows the time zone and the pseudonyms.</summary>
    private sealed class Doc(TimeZoneInfo zone, Pseudonymizer names)
    {
        private readonly StringBuilder _sb = new();

        public TimeZoneInfo Zone { get; } = zone;

        public Pseudonymizer Names { get; } = names;

        public DateTime Local(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, Zone).DateTime;

        public string LocalTime(DateTimeOffset utc) => Local(utc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        public string Both(DateTimeOffset utc) =>
            I($"{utc.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC ({LocalTime(utc)} local)");

        public Doc Line(string text)
        {
            _sb.Append(text).Append('\n');
            return this;
        }

        public Doc Blank() => Line(string.Empty);

        public Doc Item(string label, string value) => Line(I($"- {label}: {value}"));

        public Doc Row(params string[] cells) => Line("| " + string.Join(" | ", cells.Select(c => c.Replace("|", "/"))) + " |");

        public Doc Rule(int columns) => Line("|" + string.Concat(Enumerable.Repeat("---|", columns)));

        public override string ToString() => _sb.ToString();
    }
}
