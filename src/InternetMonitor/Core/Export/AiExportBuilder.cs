using System.Globalization;
using System.Text;
using InternetMonitor.Configuration;
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
/// <summary>
/// How much detail the document carries - the trade-off between completeness and fitting into an
/// AI's window. Compact and Standard keep the newest incidents in full and the rest as one line
/// each, shorten long timelines and cap the speed-test rows; Full leaves nothing out.
/// </summary>
public enum AiExportDetail
{
    Compact,
    Standard,
    Full,
}

public static class AiExportBuilder
{
    /// <summary>What a detail level allows; <see cref="int.MaxValue"/> means no limit.</summary>
    private sealed record Limits(int FullIncidents, int TimelineHead, int TimelineTail, int SpeedTestRows)
    {
        public static Limits For(AiExportDetail detail) => detail switch
        {
            AiExportDetail.Compact => new(10, 10, 10, 30),
            AiExportDetail.Standard => new(20, 20, 20, 60),
            // Head and tail are added together, so they stay far from int.MaxValue.
            _ => new(int.MaxValue, 1_000_000, 1_000_000, int.MaxValue),
        };
    }

    /// <summary>What the limits cut, counted up front so "About this export" can say so before the sections come.</summary>
    private sealed record Trimmed(AiExportDetail Detail, int IncidentsSummarised, int TimelinesShortened, int TimelineEntriesLeftOut, int SpeedTestRowsLeftOut)
    {
        public bool Anything => IncidentsSummarised > 0 || TimelinesShortened > 0 || SpeedTestRowsLeftOut > 0;

        public static Trimmed Count(AiExportDetail detail, Limits limits, IReadOnlyList<Incident> incidents, int speedTests)
        {
            List<Incident> newestFirst = [.. incidents.OrderByDescending(i => i.StartUtc)];
            List<Incident> full = [.. newestFirst.Take(limits.FullIncidents)];
            int shortened = full.Count(i => i.Timeline.Count > limits.TimelineHead + limits.TimelineTail);
            int entriesLeftOut = full.Where(i => i.Timeline.Count > limits.TimelineHead + limits.TimelineTail).Sum(i => i.Timeline.Count - limits.TimelineHead - limits.TimelineTail);
            return new Trimmed(detail, Math.Max(0, newestFirst.Count - full.Count), shortened, entriesLeftOut, Math.Max(0, speedTests - limits.SpeedTestRows));
        }
    }

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
           - packet loss and jitter: steady low-level loss (line or Wi-Fi quality) versus bursts
             at particular times (congestion);
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

        This document may be shortened to fit your window - "About this export" says what was left
        out. If a finding needs more detail than is here, say so: the user can give you the full
        "export all data" zip, which holds every incident and every measurement.
        """;

    private const string Closing = """
        ## Reminder for the AI

        Now produce the five parts listed under "Instructions for the AI": summary, findings with
        evidence and confidence, ranked root causes, recommendations with who should act, and what
        to measure next. Ground every claim in the data above, cite incident ids and dates, and say
        where the data cannot answer.
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

        **Incidents.** An incident opens after three consecutive failing cycles and closes after two
        consecutive good ones, so very short blips do not appear. Each records its most likely
        cause, a timeline, every step's result at the start and at resolution, and what changed in
        the environment in the 30 minutes before it opened. Each also carries this computer's own
        traffic around it: a baseline (the 15 minutes before the 5 minutes before it opened), those
        5 minutes, the incident itself, and the 5 minutes after it closed - average and peak in
        Mbit/s, and as a share of the line's capacity (the last speed test, else the adapter's link
        speed). A high load just before a fault points at the router or at bufferbloat rather than
        at the provider; an idle line that fails does not. Only this computer's traffic is visible:
        a quiet line here can still be full because of another device.

        **Causes and where they originate.**
        - This computer: Adapter, Link (cable or Wi-Fi), IpConfiguration.
        - Home network / router: Gateway (the router did not answer - this may be the provider's
          own equipment).
        - Provider: Internet (router fine, nothing beyond it answered), Dns (the DNS server failed),
          Target (the HTTPS reachability check failed while the steps before it worked).
        - Metered connection: the same causes while the computer was on mobile data or a phone's
          hotspot - the mobile network, not the line; these do not count against availability.
        - Other: CaptivePortal (a login page on hotel/train/guest Wi-Fi), Unknown.
        - Not outages: FirewallSuspected (pings filtered while real traffic works),
          Ipv6Broken (the network offers IPv6 but it does not work while IPv4 does - browsers try
          IPv6 first, so sites load slowly; usually the router or the provider) and
          NetworkHealth (packet-level warnings: retransmissions, loss, TTL errors, rogue DHCP).

        **Names in this document.** Wi-Fi network names, access points, hardware addresses and any
        public address of this computer are replaced by stable stand-ins: "Wi-Fi network A",
        "AP-1", "MAC-1", "IP-1". The same stand-in always means the same network, device or
        address, so you can follow them across incidents; they are not missing data. The public
        ping targets, the public DNS resolvers and the user's own DNS servers are left readable.
        A VPN appears as "VPN (all traffic)" or "VPN (some traffic only)". Traceroute hops are the
        provider's routers and are shown as they are.

        **Periods.** The summary, the per-day tables and the Wi-Fi section cover the summary period
        named in "About this export" (as far back as history is kept). The incidents and the table
        of all recorded series cover everything ever kept, which can reach further back.

        **Percentiles** are nearest-rank over the samples in the row.

        **Series units.** Latency series in milliseconds; adapter/link are 1 (up) or 0 (down);
        Wi-Fi signal is Windows' signal quality in percent; traffic and speed tests in Mbit/s;
        "extra ping under load" is how many milliseconds the round trip grew while a speed test
        filled the line (A under 5 ms, B under 30, C under 60, D under 200, F 200 or more).

        **IPv6.** Only tested when the network hands out a global IPv6 address: a ping to public
        IPv6 resolvers in step 5 and a plain HTTP request over IPv6 in step 7. The "IPv6" series is
        1 when that request worked and 0 when it failed; it is absent on an IPv4-only network.

        **Packet loss and jitter.** Once a minute ten pings, 100 ms apart, go to the first public
        address step 5 uses. Packet loss is the percentage without a reply; jitter is the mean
        difference in round trip between consecutive replies. A burst where nothing at all came
        back is not recorded (that is an outage, or ICMP being filtered), so these series describe
        the quality of a working line. Around 1% loss or 30 ms jitter is where calls and games
        start to suffer.

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
        DateTimeOffset? now = null,
        AiExportDetail detail = AiExportDetail.Standard) =>
        Compose("# Internet Monitor - Data Export for AI Analysis", Instructions, incidents, historySeries, settings, appVersion, timeZone, now, detail);

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
        DateTimeOffset? now = null,
        AiExportDetail detail = AiExportDetail.Standard) =>
        Compose("# Internet Monitor - Data Export", null, incidents, historySeries, settings, appVersion, timeZone, now, detail);

    private static string Compose(
        string title,
        string? instructions,
        IReadOnlyList<Incident> incidents,
        IReadOnlyDictionary<string, IReadOnlyList<MetricPoint>> history,
        AppSettings settings,
        string appVersion,
        TimeZoneInfo? timeZone,
        DateTimeOffset? now,
        AiExportDetail detail)
    {
        TimeZoneInfo zone = timeZone ?? TimeZoneInfo.Local;
        DateTimeOffset to = now ?? DateTimeOffset.UtcNow;
        // The summary sections look back as far as history is kept.
        DateTimeOffset from = to - TimeSpan.FromDays(settings.HistoryRetentionDays);
        IReadOnlyList<MetricPoint> Series(string id) => history.TryGetValue(id, out IReadOnlyList<MetricPoint>? points) ? points : [];

        // The app's own public addresses stay readable in the export; see Pseudonymizer.
        var targets = new CheckTargets();
        var names = new Pseudonymizer([.. targets.PublicIpTargets, .. targets.PublicResolvers, .. settings.ParsedPublicIpTargets(), .. settings.ParsedCustomDnsServers()]);
        names.Learn(incidents, Series(SeriesNames.WifiSignal));

        ProviderReportData report = ProviderReportData.Compute(incidents, Series, from, to, zone);
        var doc = new Doc(zone, names);
        Limits limits = Limits.For(detail);
        Trimmed trimmed = Trimmed.Count(detail, limits, incidents, report.SpeedTests.Count);

        doc.Line(title).Blank();
        if (instructions is not null)
        {
            doc.Line(instructions).Blank();
        }

        AppendContext(doc, settings, appVersion, from, to, trimmed);
        doc.Line(HowToRead).Blank();
        AppendSummary(doc, report, settings);
        AppendOutagePatterns(doc, report);
        AppendIncidents(doc, incidents, limits, Series, to, TimeSpan.FromSeconds(settings.BackgroundIntervalSeconds));
        AppendLatency(doc, Series, from, to);
        AppendLineQuality(doc, report);
        AppendSpeedTests(doc, report, settings, limits);
        AppendWifi(doc, Series(SeriesNames.WifiSignal), from, to);
        AppendTraffic(doc, Series(SeriesNames.TrafficDown), Series(SeriesNames.TrafficUp), report, from, to);
        AppendSeriesStatistics(doc, history);
        AppendNetworkHealth(doc, incidents, settings);

        if (instructions is not null)
        {
            // Long documents: the task is followed better when it is repeated at the end.
            doc.Line(Closing).Blank();
        }

        return doc.ToString();
    }

    // ---- Sections -------------------------------------------------------------------------------

    /// <summary>
    /// When, from which version and with which settings the data was collected - what an AI needs to
    /// read the numbers in the rest of the document.
    /// </summary>
    private static void AppendContext(Doc doc, AppSettings settings, string appVersion, DateTimeOffset from, DateTimeOffset to, Trimmed trimmed)
    {
        doc.Line("## About this export").Blank()
            .Item("Generated", doc.Both(to))
            .Item("Summary period", $"{doc.Both(from)} to {doc.Both(to)} (last {(to - from).TotalDays:F0} days)")
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
            .Item("Network-health monitoring", settings.NetworkHealthMonitoringEnabled ? string.Join(", ", settings.EnabledNetworkHealthConditions) : "off")
            .Item("Detail level", trimmed.Detail.ToString().ToLowerInvariant());

        if (trimmed.Anything)
        {
            var cuts = new List<string>();
            if (trimmed.IncidentsSummarised > 0)
            {
                cuts.Add(I($"{trimmed.IncidentsSummarised} older incidents are summarised to one line each"));
            }

            if (trimmed.TimelinesShortened > 0)
            {
                cuts.Add(I($"{trimmed.TimelinesShortened} timelines are shortened ({trimmed.TimelineEntriesLeftOut} entries left out, marked where they were)"));
            }

            if (trimmed.SpeedTestRowsLeftOut > 0)
            {
                cuts.Add(I($"{trimmed.SpeedTestRowsLeftOut} speed-test rows are left out (a per-week table covers them)"));
            }

            doc.Item("Left out to fit", string.Join("; ", cuts) + ". The full data is in the \"export all data\" zip.");
        }
        else
        {
            doc.Item("Left out to fit", "nothing was left out");
        }

        doc.Blank();
    }

    /// <summary>
    /// The provider report's headline figures: coverage, availability, outages per side and per cause,
    /// and speed tests below 80% of the subscription.
    /// </summary>
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

    /// <summary>
    /// When outages start, by local hour and weekday (Monday first) - the shape of evening congestion
    /// or a nightly router restart.
    /// </summary>
    private static void AppendOutagePatterns(Doc doc, ProviderReportData report)
    {
        doc.Line("### When outages start (local time)").Blank();
        if (report.Outages.Count == 0)
        {
            doc.Line("No outages in the period.").Blank();
            return;
        }


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

    private static void AppendIncidents(Doc doc, IReadOnlyList<Incident> incidents, Limits limits, Func<string, IReadOnlyList<MetricPoint>> series, DateTimeOffset now, TimeSpan sampleInterval)
    {
        doc.Line("## Incidents (all recorded, newest first)").Blank();
        if (incidents.Count == 0)
        {
            doc.Line("No incidents recorded.").Blank();
            return;
        }

        List<Incident> newestFirst = [.. incidents.OrderByDescending(i => i.StartUtc)];
        List<Incident> full = [.. newestFirst.Take(limits.FullIncidents)];
        List<Incident> summarised = [.. newestFirst.Skip(limits.FullIncidents)];

        // Every free-text field goes through doc.Names, which swaps network names and hardware
        // addresses for their stand-ins; measurement labels and fixed values are translated back
        // to English, since they were recorded in whatever language the app showed at the time.
        foreach (Incident incident in full)
        {
            string side = IncidentSide(incident);
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

            if (incident.Note is { Length: > 0 } note)
            {
                doc.Item("User's note", doc.Names.Text(note));
            }

            if (incident.Traceroute is { } route)
            {
                // Hop addresses are the provider's routers, not the user's, so they stay as they are.
                doc.Line(I($"- Route to {route.Target} when it opened ({(route.Reached ? "reached" : "did not reach the target")}):"));
                foreach (string hop in route.Describe("no answer"))
                {
                    doc.Line("  - " + hop.Trim());
                }
            }

            if (incident.RecentChanges.Count > 0)
            {
                doc.Line("- Changed in the 30 minutes before (local time):");
                foreach (string change in incident.RecentChanges)
                {
                    doc.Line("  - " + doc.Names.Text(change));
                }
            }

            if (IncidentThroughputSummary.Resolve(incident, series, now, sampleInterval) is { } traffic)
            {
                doc.Line("- This computer's own traffic around it (Mbit/s download / upload; only this computer is visible):");
                foreach (string line in traffic.Describe(LocalizationManager.GetEnglish, LocalizationManager.FormatEnglish))
                {
                    doc.Line("  - " + line);
                }
            }

            if (incident.Timeline.Count > 0)
            {
                doc.Line("- Timeline:");
                int leftOut = incident.Timeline.Count - limits.TimelineHead - limits.TimelineTail;
                for (int i = 0; i < incident.Timeline.Count; i++)
                {
                    if (leftOut > 0 && i == limits.TimelineHead)
                    {
                        doc.Line(I($"  - ... {leftOut} entries left out ..."));
                        i += leftOut - 1;
                        continue;
                    }

                    TimelineEntry entry = incident.Timeline[i];
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

        if (summarised.Count == 0)
        {
            return;
        }

        doc.Line("### Older incidents (one line each)").Blank()
            .Line(I($"{summarised.Count} older incidents, newest first. Their full detail is in the \"export all data\" zip."))
            .Blank()
            .Row("Id", "Cause", "Side", "Start (local)", "Duration", "Load before", "Headline").Rule(7);
        foreach (Incident incident in summarised)
        {
            // This computer's own load in the five minutes before, as a share of the line's capacity.
            double? loadBefore = IncidentThroughputSummary.Resolve(incident, series, now, sampleInterval)?.Before?.PeakUtilization;
            doc.Row(
                incident.Id,
                incident.ConditionKey is { } condition ? I($"{incident.Cause} ({condition})") : incident.Cause.ToString(),
                IncidentSide(incident),
                doc.LocalTime(incident.StartUtc),
                incident.Duration is { } d ? Duration(d) : "ongoing",
                loadBefore is { } share ? IncidentThroughput.Percent(share) : "-",
                doc.Names.Text(incident.Headline));
        }

        doc.Blank();
    }

    private static string IncidentSide(Incident incident) =>
        incident.ConditionKey is null ? SideName(ProviderReportData.SideOf(incident.Cause)) : "packet-level warning";

    /// <summary>
    /// Latency per local day for the four round-trip series, and the public-internet latency by hour of
    /// day over the whole period.
    /// </summary>
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
        doc.Line("## Latency per day (ms, local dates)").Blank();
        if (inPeriod.Values.All(p => p.Count == 0))
        {
            doc.Line("Not measured in this period.").Blank();
            return;
        }

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

    /// <summary>Packet loss and jitter per day, from the provider report's figures. Left out before the series existed.</summary>
    private static void AppendLineQuality(Doc doc, ProviderReportData report)
    {
        doc.Line("## Packet loss and jitter").Blank();
        if (report.QualityByDay.Count == 0)
        {
            doc.Line("Not measured in this period.").Blank();
            return;
        }

        doc.Item("Average packet loss", I($"{report.AverageLossPercent:F2}% over {report.QualityByDay.Sum(d => d.Bursts)} one-minute bursts"))
            .Blank()
            .Row("Date", "Loss %", "Bursts with loss", "Bursts", "Jitter ms").Rule(5);
        foreach (DayQuality day in report.QualityByDay)
        {
            doc.Row(
                day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                I($"{day.LossPercent:F2}"),
                I($"{day.BurstsWithLoss}"),
                I($"{day.Bursts}"),
                day.JitterMs is { } jitter ? I($"{jitter:F1}") : "-");
        }

        doc.Blank();
    }

    /// <summary>Every speed test in the period, against the subscription and with its bufferbloat grade.</summary>
    private static void AppendSpeedTests(Doc doc, ProviderReportData report, AppSettings settings, Limits limits)
    {
        doc.Line("## Speed tests").Blank();
        if (report.SpeedTests.Count == 0)
        {
            doc.Line("No speed tests in the period.").Blank();
            return;
        }

        List<SpeedTestEntry> ordered = [.. report.SpeedTests.OrderBy(t => t.AtUtc)];
        bool trimmed = ordered.Count > limits.SpeedTestRows;
        if (trimmed)
        {
            doc.Line(I($"{ordered.Count} tests in the period; the latest {limits.SpeedTestRows} are listed, the per-week table below covers all of them.")).Blank();
        }

        doc.Row("When (local)", "Download Mbit/s", "Upload Mbit/s", "% of subscribed down", "Ping ms", "Extra ping under load").Rule(6);
        foreach (SpeedTestEntry test in trimmed ? ordered.TakeLast(limits.SpeedTestRows) : ordered)
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
        if (!trimmed)
        {
            return;
        }

        doc.Line("### Per week (all tests in the period)").Blank()
            .Row("Week", "Tests", "Download avg / min Mbit/s", "Upload avg Mbit/s", "Ping avg ms").Rule(5);
        foreach (IGrouping<string, SpeedTestEntry> week in ordered.GroupBy(t => IsoWeek(doc.Local(t.AtUtc))).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            double[] pings = [.. week.Where(t => t.PingMs is not null).Select(t => t.PingMs!.Value)];
            doc.Row(
                week.Key,
                I($"{week.Count()}"),
                I($"{week.Average(t => t.DownloadMbps):F1} / {week.Min(t => t.DownloadMbps):F1}"),
                I($"{week.Average(t => t.UploadMbps):F1}"),
                pings.Length > 0 ? I($"{pings.Average():F0}") : "-");
        }

        doc.Blank();
    }

    private static string IsoWeek(DateTime local) => I($"{ISOWeek.GetYear(local)}-W{ISOWeek.GetWeekOfYear(local):00}");

    /// <summary>
    /// Signal statistics, time spent at weak/medium/good signal, and every switch of access point -
    /// the access points under their stand-in names. Left out when no Wi-Fi signal was recorded in the period.
    /// </summary>
    private static void AppendWifi(Doc doc, IReadOnlyList<MetricPoint> wifi, DateTimeOffset from, DateTimeOffset to)
    {
        List<MetricPoint> points = [.. wifi.Where(p => p.Timestamp >= from && p.Timestamp <= to)];
        doc.Line("## Wi-Fi").Blank();
        if (points.Count == 0)
        {
            doc.Line("Not measured in this period (no Wi-Fi, or a cable).").Blank();
            return;
        }

        double[] values = [.. points.Select(p => p.Value).Order()];
        doc.Item("Signal (Windows quality %)", I($"min {values[0]:F0}, average {values.Average():F0}, p5 {Percentile(values, 0.05):F0}, max {values[^1]:F0} over {values.Length} samples"))
            .Item("Share of samples below 40% / 40-60% / above 60%", I($"{Pct(Share(values, v => v < 40), 1)} / {Pct(Share(values, v => v is >= 40 and <= 60), 1)} / {Pct(Share(values, v => v > 60), 1)}"));

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

    /// <summary>
    /// This computer's own traffic, and how often it filled most of the line - context for latency
    /// that rises under load.
    /// </summary>
    private static void AppendTraffic(Doc doc, IReadOnlyList<MetricPoint> down, IReadOnlyList<MetricPoint> up, ProviderReportData report, DateTimeOffset from, DateTimeOffset to)
    {
        double[] d = [.. down.Where(p => p.Timestamp >= from && p.Timestamp <= to).Select(p => p.Value).Order()];
        double[] u = [.. up.Where(p => p.Timestamp >= from && p.Timestamp <= to).Select(p => p.Value).Order()];
        doc.Line("## This computer's own traffic (Mbit/s, each point an average over its recording interval)").Blank();
        if (d.Length == 0)
        {
            doc.Line("Not measured in this period.").Blank();
            return;
        }

        // Each point also carries the fastest single cycle inside its interval, which is where a short burst shows.
        double peakDown = down.Where(p => p.Timestamp >= from && p.Timestamp <= to).Max(TrafficHistory.PeakOf);
        double peakUp = up.Where(p => p.Timestamp >= from && p.Timestamp <= to).Select(TrafficHistory.PeakOf).DefaultIfEmpty(0).Max();
        doc.Item("Download", I($"average {d.Average():F1}, p95 {Percentile(d, 0.95):F1}, max {d[^1]:F1}"))
            .Item("Upload", u.Length > 0 ? I($"average {u.Average():F1}, p95 {Percentile(u, 0.95):F1}, max {u[^1]:F1}") : "-")
            .Item("Fastest single cycle", I($"download {peakDown:F1}, upload {peakUp:F1}"));

        if (report.SpeedTests.LastOrDefault() is { DownloadMbps: >= 1 } latest)
        {
            doc.Item("Samples above 70% of the last measured download speed", I($"{Pct(Share(d, v => v >= latest.DownloadMbps * 0.7), 1)}"));
        }

        doc.Blank();
    }

    /// <summary>
    /// One row per recorded series over everything kept - not only the summary period - including v1's
    /// legacy series, so nothing in the history is invisible to the reader.
    /// </summary>
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

    /// <summary>The packet-level warnings by condition. Left out when monitoring is off and none were ever recorded.</summary>
    private static void AppendNetworkHealth(Doc doc, IReadOnlyList<Incident> incidents, AppSettings settings)
    {
        doc.Line("## Network-health warnings").Blank();
        if (!settings.NetworkHealthMonitoringEnabled && !incidents.Any(i => i.ConditionKey is not null))
        {
            doc.Line("Not monitored: every packet-level condition is switched off, and none was ever recorded.").Blank();
            return;
        }

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
        OutageSide.MeteredConnection => "metered connection (mobile data or hotspot) - not the line",
        _ => "other",
    };

    /// <summary>English names for the series ids - several are v1's probe ids, kept for on-disk continuity (see <see cref="SeriesNames"/>).</summary>
    private static string FriendlyName(string seriesId) =>
        SeriesNames.Chartable.FirstOrDefault(s => s.Id == seriesId) is { } series
            ? LocalizationManager.GetEnglish(series.NameKey)
            : seriesId switch
            {
                SeriesNames.Metered => "Metered connection (mobile data / hotspot)",
                SeriesNames.Outage => "Incident open (step pipeline)",
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
        SeriesNames.Metered => "1 = metered, 0 = not",
        SeriesNames.Outage => "1 = incident open, 0 = not",
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
