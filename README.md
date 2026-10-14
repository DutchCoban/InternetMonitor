# Internet Monitor

A lightweight Windows tray application that monitors network and internet connectivity,
diagnoses *where* a problem sits in the network stack (not just "internet is down"), and keeps
a historical record of outages. It ships as a single self-contained `.exe` with no external
dependencies to install.

Internet Monitor is a generic network diagnostics tool - no organization's endpoints or
branding are built into it. Any application-specific checks (an internal API, a company
website, ...) are added by the user through Settings. It's an independent open-source project,
not affiliated with or endorsed by any company.

This repository went through a ground-up rewrite: v1 (still kept under `InternetMonitor/` for
reference) was the original; **v2** (`src/InternetMonitor/`, what the rest of this README
describes, and what ships from here on) replaced it with a redesigned interface and an adaptive
check cadence, and is still under **active development** rather than a one-time port - most of
v1's functionality is back or improved on, a handful of pieces aren't (yet, or possibly at all) -
see [Project status](#project-status) for exactly what and why.

## What it checks

Every few seconds (3s while a window is open, 15s in the background - see
[Adaptive cadence](#adaptive-cadence)), seven independent probes run and their results are
combined into a single diagnosis, working outward through the network stack so a single failure
is attributed to its most likely cause instead of being reported as a generic "internet is down":

1. **Network adapter** - is an adapter present, operational, and actually carrying traffic?
2. **Cable / link** - does the adapter report a physical link at all?
3. **IP address** - does the adapter have a usable IPv4 address (vs. an APIPA `169.254.x.x` address, which means DHCP failed)?
4. **Gateway** - is the default gateway known and reachable? (Races ICMP and TCP - see below.)
5. **Public internet (multi-endpoint)** - ICMP pings to `9.9.9.9`, `8.8.8.8`, and `1.1.1.1` by default (configurable in Settings → Public internet). A single unreachable endpoint doesn't mean internet is down; the diagnosis only escalates once none of them respond.
6. **DNS** - can a hostname (`www.ripe.net` by default) actually be resolved by the DNS servers
   this adapter has, obtained via DHCP or set by hand? Those servers are asked directly. On
   failure, the same question goes to three public resolvers (`9.9.9.9`, `1.1.1.1`, `8.8.8.8`): if
   they answer, it's your network's DNS server; if they don't either, it's DNS as a whole.
7. **Target** - a built-in general-reachability check (a `generate_204` endpoint, chosen specifically because it isn't scored/throttled like a normal website behind a CDN - see the doc comment on `CheckTargets.Targets` for why that matters), plus any HTTPS or TCP/UDP port endpoints you've configured in Settings. Every URL target gets a full, per-stage-timed DNS → TCP → TLS → HTTP request.

All seven steps describe **one path**: the adapter Windows routes internet traffic over is
chosen once per cycle, and steps 4-7 send everything from that adapter's own address and resolve
names through that adapter's own DNS servers. On a machine with several adapters up at once -
say WiFi for internet plus a USB-Ethernet adapter used to configure a switch - the diagnosis
therefore never mixes facts about one adapter with traffic over another. If the chosen adapter
has no way out, step 5 fails ("no internet") instead of a later step failing for a misleading
reason.

Because a network firewall can block ICMP without blocking real traffic, the diagnosis engine
specifically compares ping results against the Target check's results: if ping fails but the
reachability check works, it reports "internet available, ICMP possibly filtered" rather than
claiming the connection is down.

## Adaptive cadence

The check cycle runs every 3 seconds while a window is open and every 15 seconds while only the
tray icon is live (both configurable in Settings → Check interval) - history is always sampled at
the slower, background rate regardless, so retention and chart density don't change depending on
whether you happen to have the window open.

## Five screens

- **Status** (left-click the tray icon): a simple green/red/amber overview with a plain-language
  explanation and a seven-step timeline - is the network, internet, DNS, and each configured
  endpoint working, and which step is being re-checked right now?
- **Diagnostics** (right-click → Diagnostics): every probe's raw measurements, the derived
  diagnosis, a network-health summary (see below), and a table you can expand per step for exact
  detail.
- **History**: a zoomable, pannable chart per check (mouse wheel to zoom, drag to pan,
  double-click to reset), switchable between any chartable series and a 15 min/1 hour/6 hour/24
  hour/7 day/all-time range. Kept for 30 days.
- **Incidents**: a run of consecutive failed diagnosis cycles (debounced, so a brief blip doesn't
  create noise) grouped into a single incident with a unique ID (`INC-yyyyMMdd-####`), a
  classification, a start/end time, and a snapshot of every probe's result at the start and at
  resolution. Incident text (headline, explanation, step snapshots) is always recorded in English
  regardless of the active display language, so an exported incident reads the same no matter who
  opens it later.
- **Tools**: a live-filling **Traceroute** (each hop optionally annotated with who owns it - your
  own LAN, your ISP, or a third party, via Team Cymru/RIR lookups, toggleable in Settings - and,
  when ICMP doesn't reach the destination at all, a plain TCP connect to the destination to tell
  "ICMP is blocked" apart from "it's actually down" - see [Known limitations](#known-limitations));
  a **Network scan** of the local subnet (which devices answer a ping, with hostname, MAC address,
  and manufacturer - looked up via two free, alternating MAC-vendor APIs, toggleable in Settings -
  for each; addresses that don't answer a ping also get a TCP-port sweep, toggleable, so a device
  that ignores ICMP but has an open port still shows up); and a **Speed test** (see below).

## Network health monitoring

Off by default (Settings → Network health). When enabled, flags packet-level conditions that sit
below what the seven steps measure: TCP retransmissions, packet loss, TTL-exceeded responses, and
an unexpected (rogue) DHCP server on the network - each with its own threshold and time window.

## Speed test

The Tools page's third tab runs a [LibreSpeed](https://librespeed.org/) test (ping, jitter,
download, upload) against a fixed public server - no server picker, to keep this feature's
configuration surface small. "Run now" is always available; Settings → Tools also has an optional
automatic run on a fixed interval (1/2/4/8/12/24 hours, off by default). Either way, a result is
recorded into History (as two series, download and upload) and included in "Export all data" -
there is no separate speed-test-only export.

## Exporting data

Settings → Export covers four kinds of export, and a fifth is available per-incident from the
Incidents screen:

- **Export all data** - a zip of settings (redacted), incidents, history, and the diagnostic log.
- **Export for AI analysis** - a Markdown report with instructions aimed at an AI assistant.
- **Export plain data** - the same report without the AI-facing instructions.
- **Per-incident export** - a Markdown report for a single incident, from the Incidents screen.
- **Network scan → Export CSV** - the LAN scan's device table (IP, hostname, MAC, vendor, response time).

## Settings backup

Settings → Configuration exports or imports this app's own configuration as JSON - separate from
the data exports above, which never include enough to actually restore a setup.

## Checking for updates

Settings → Updates checks GitHub for a newer release once a day by default (configurable), plus a
manual "Check for updates" in the tray menu. Either way, a pop-up reports what it found with a
link to the release page. A separate "Update automatically" toggle (off by default) goes further:
when a newer release is found, it's downloaded in the background, and after a short countdown
pop-up the app replaces itself and restarts on its own.

## Diagnostic logging

Optional, off by default. Configurable levels in Settings → Logging:

| Level | What's recorded |
|---|---|
| Off | Nothing |
| Basic | Status changes and incident open/close events only |
| Extended | + every periodic probe result |
| Full | + verbose details even for successful checks |

Logs are written as JSON Lines to `%AppData%\InternetMonitor\logs\diagnostics.log`, rotated by
size with a configurable number of retained files. A logging failure never interrupts
monitoring. No secrets, tokens, or credentials are ever logged - only connectivity-relevant
technical data (addresses, timings, status codes, error text).

## Configuring application endpoints

Settings → Targets lets you add, edit, enable/disable, and remove any number of application
endpoints, of two kinds:

- **HTTPS** - a full DNS → TCP → TLS → HTTP check against a URL, same as the built-in
  reachability check, with optional custom headers (for an API that needs auth) and an optional
  expected status code.
- **Port (TCP/UDP)** - plain reachability of a host:port, for services that aren't HTTP (a
  database, an internal TCP service, ...). TCP treats both a successful connect and an actively
  refused connection as "reachable" (either proves something answered); UDP is inherently
  ambiguous, so a plain timeout is reported as "uncertain" rather than a false success/failure.

Each enabled endpoint is checked every cycle alongside the built-in checks and shown by name (not
raw URL/host) on the Status screen.

## Uptime Kuma heartbeat

Settings → Integrations lets you configure a push-monitor URL and interval (minimum 60 seconds).
When a URL is set, the app sends a plain periodic `GET` to it - no dependency on any connectivity
check, matching how Uptime Kuma push monitors are normally used. Off by default (no URL
configured). See the [Uptime Kuma project](https://github.com/louislam/uptime-kuma) for what it is
and how to set up a push monitor to get a URL from.

## Other settings

- **Language** (General) - English, Dutch, German, or Polish. Switching takes effect immediately
  across the whole app, including an already-open Settings window.
- **Theme** (General) - follows Windows, or forced light/dark.
- **Incident pop-ups** (General) - a brief on-screen notice when a new incident opens.

## Project status

v2 is a from-scratch rewrite, not an incremental update, and it's still being actively built out -
this section exists so that's never a surprise. Settings, incident history, and per-check history
all carry over automatically from an existing v1 installation regardless of what's below; nothing
here affects that.

**Already in v2, beyond what v1 had**: an adaptive (3s/15s, both configurable) check cadence
instead of a fixed 15-second cycle; network-health monitoring (TCP retransmissions, packet loss,
TTL errors, rogue DHCP); traceroute hop ownership; LAN-scan manufacturer lookup and a TCP fallback
for devices that ignore ICMP (plus the same TCP fallback for traceroute, see
[Known limitations](#known-limitations)); four export formats plus a settings backup; and a fully
automatic self-update option (v1 only ever linked to the release page).

**Back in v2 after initially being left out**: the Speed Test (LibreSpeed) check - manual and
automatic, see [Speed test](#speed-test) above. Its return is the precedent for the rest of this
list: "not yet in v2" has not meant "gone for good" so far, and there's no reason to assume it
always will.

**Not currently in v2** - each was left out of the initial rewrite because it measured something a
general-purpose tool shouldn't assume every user wants running by default, not because it was
rejected outright. Whether any of these come back depends on whether anyone (including future me)
decides it's worth the added settings-surface and maintenance:

- Continuous named ping targets with their own per-target latency graphs.
- The Wi-Fi signal row (SSID/BSSID/signal quality/link speed/PHY type).
- Quiet hours (suppressing incident pop-ups during a configured daily window).
- The SLA/uptime report (percentage uptime over 7/30/90 days, with exclusions).
- Configurable history retention - v1 offered 15 minutes to 32 days; v2 is a fixed 30 days.

If you're relying on one of these in v1 and it matters to you whether/when it returns, open an
issue - that's a better signal than silence either way.

## Project structure

For a new contributor, the call chain from startup to a diagnosis on screen is:

- **`Program.cs`** - entry point. Installs global exception handlers, enforces a single running
  instance (`App/SingleInstanceManager.cs`), then hands off to...
- **`App/AppHost.cs`** - the composition root. Owns the tray icon/menu, loads `AppSettings`, and
  constructs and wires together everything below.
- **`Core/Steps/StepPipeline.cs`** - runs the seven checks under `Core/Steps/Checks/` (see
  `Core/Steps/IStepCheck.cs` for the check/result shape) concurrently every cycle and assembles a
  `StepSnapshot`.
- **`Core/Diagnosis/RootCauseAnalyzer.cs`** - a pure function that walks the snapshot bottom-up
  and returns the single most likely root cause, rather than reporting every failed check as its
  own symptom; `Core/Diagnosis/PlainLanguage.cs` turns that into the headline/explanation shown on
  screen.
- From there, `Core/Incidents/IncidentTracker.cs` turns a run of failing cycles into a tracked
  `Incident` (persisted via `IncidentStore`), `Core/Diagnosis/DiagnosticLogger.cs` optionally
  writes a detailed JSONL log, and the `Ui/Shell/Pages/` screens render the result - everything
  crossing from a background thread onto the UI thread is marshalled via `SynchronizationContext`.

Besides the main cycle, a handful of things run on their own schedule: `Core/Monitors/KumaPusher.cs`
(the heartbeat push), `Core/Monitors/NetworkHealthMonitor.cs` (packet-level conditions),
`Core/Updates/UpdateChecker.cs` (the daily GitHub check), `Core/Monitors/SpeedTestScheduler.cs`
(the optional automatic Speed Test) - all four follow the same `Reconfigure(...)`-driven
live-restart shape, so a Settings change takes effect immediately without restarting the app.
Traceroute (`Core/Tools/Traceroute/`), the LAN scanner (`Core/Tools/LanScan/`) and the Speed Test
itself (`Core/Tools/SpeedTest/`) are the genuinely on-demand tools, triggered only from the Tools
page (the Speed Test's own client is also what the scheduler above calls headlessly).

All user-facing text (including diagnosis headlines/explanations) goes through
`Localization/LocalizationManager.cs`, loading `Localization/{en,nl,de,pl}.json` - four languages,
switchable live from Settings; there is no hardcoded UI language anywhere in the check/diagnosis
layer. Every colour, spacing step, corner radius and font comes from `Ui/Theming/Theme.cs` - no
control names a color of its own.

## Building and running

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build InternetMonitor.sln
```

Publish a single self-contained `.exe` (no .NET runtime needed on the target machine):

```powershell
dotnet publish src/InternetMonitor/InternetMonitorV2.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish
```

The result, `publish/InternetMonitor.exe`, can be copied to and run on any Windows 10/11 x64
machine. This intentionally skips `PublishReadyToRun` (which roughly doubles framework assembly
sizes by embedding precompiled native code) to keep the download small - a slightly slower JIT
cold start is an easy trade for a tray app that starts once and runs for days.

## Settings and data locations

- Settings: `%AppData%\InternetMonitor\settings.json` (the same file v1 used - an existing
  installation's settings are picked up automatically)
- Diagnostic log: `%AppData%\InternetMonitor\logs\diagnostics.log` (+ rotated `.log.1`, `.log.2`, ...)
- Incident history: `%AppData%\InternetMonitor\incidents.json`
- Per-check history (the History screen's charts): `%AppData%\InternetMonitor\history\`
- Cached lookups (traceroute hop ownership, MAC vendors): `%AppData%\InternetMonitor\cache\`
- Saved traceroute runs: `%AppData%\InternetMonitor\traceroutes\`

None of these live inside the application directory, so the app itself can be replaced/updated
(by hand, or via the self-update feature above) without touching configuration or history.

## Known limitations

- Gateway reachability races a plain ICMP ping against a TCP connect attempt to a few common
  ports (80/443/53), all at once - whichever answers first wins - so it works both on networks
  where ICMP is fine (the common case) and ones that filter it, without waiting through both in
  sequence. A gateway that answers neither will still read as unreachable, but that combination
  is rare in practice. This check has no retry: a network settings change (even just the DNS
  server) can make the gateway genuinely unreachable at the OS level for a few seconds, and
  that's real, momentary state worth showing, not a measurement error to hide.
- The DNS step queries the adapter's DNS servers directly over plain UDP port 53, not through
  Windows' own resolver. That's what lets it test exactly that adapter's servers and report which
  one answered, but it also means a setup that only resolves through Windows' encrypted DNS
  (DNS over HTTPS) with plain DNS blocked would show a DNS failure here even though browsing works.
  An adapter that has no DNS server at all is reported as exactly that.
- Application endpoints are entirely user-configured with no target allowlist, by design (this
  is a local, single-user tool) - see [Security notes](#security-notes) below.
- The self-update feature assumes the GitHub release it finds has exactly one asset named
  `InternetMonitor.exe` (falling back to the first `.exe`-named asset otherwise) - the shape every
  release has had so far.
- Traceroute does not fall back to a real TCP-based traceroute when ICMP is blocked. A genuine
  per-hop TCP traceroute needs a raw socket to catch the ICMP "Time Exceeded" replies a TCP probe
  provokes, and raw sockets need administrator rights on Windows - more than this app's deliberately
  unelevated (`asInvoker`) model is worth changing for one tool. Instead, when the ICMP trace
  doesn't reach the destination, one plain TCP connect straight to the destination (port 443, then
  80) tells you whether it's ICMP that's blocked or the destination that's actually unreachable -
  not which routers are in between.

## Security notes

- **User-configured targets are trusted.** Application endpoints are entirely user-supplied and
  flow directly into raw TCP/UDP/HTTPS probes with no target allowlist. This is intended - it's a
  local, single-user monitoring tool, and you're only ever pointing it at hosts you choose. It
  does mean that importing a settings file you don't trust would let it probe whatever hosts that
  file names.
- **Endpoint URLs can end up in incident/log files.** If a monitored endpoint's URL embeds a
  secret (an API key or webhook token in the query string, for example), that full URL is
  captured verbatim into `incidents.json` and, if detailed diagnostic logging is enabled, into
  `diagnostics.log`. Avoid putting secrets directly in monitored URLs, or keep detailed logging
  off for such endpoints. The Uptime Kuma push URL is handled separately and is never logged.
- **TLS validation is never bypassed.** Every HTTPS check uses .NET's default certificate
  validation with no custom callback.
- **Several features send third parties something about your network, each with its own
  off switch.** Traceroute's hop-ownership lookup sends hop IP addresses to Team Cymru and the
  regional internet registries (toggleable in Settings → Tools); the LAN scanner's manufacturer
  lookup sends each device's MAC prefix - never the full address - to macvendorlookup.com and
  macvendors.com (same toggle group); and the update checker sends nothing but a plain request to
  GitHub's public release API. All three are on by default except where noted, and every one is
  switchable.
- **Speed Test moves real data to a third-party server.** Unlike every other check, a speed test
  (manual or automatic) transfers real download/upload traffic to a public LibreSpeed server, not
  just small probe packets. It never runs unless "Run now" is clicked or the automatic interval is
  explicitly turned on in Settings, and the automatic interval can't be set below 1 hour.
- The app runs unelevated (`asInvoker`, see `app.manifest`) and only ever talks outbound over
  the network - it opens no listening ports and requires no special permissions, except that the
  optional self-update feature replaces its own executable file on disk.

## License

MIT - see [LICENSE](LICENSE).
