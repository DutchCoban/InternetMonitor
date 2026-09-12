# Internet Monitor

A lightweight Windows tray application that monitors network and internet connectivity,
diagnoses *where* a problem sits in the network stack (not just "internet is down"), and keeps
a historical record of outages. It ships as a single self-contained `.exe` with no external
dependencies to install.

Internet Monitor is a generic network diagnostics tool - no organization's endpoints or
branding are built into it. Any application-specific checks (an internal API, a company
website, ...) are added by the user through Settings. It's an independent open-source project,
not affiliated with or endorsed by any company.

## What it checks

Every 15 seconds, a set of independent probes run and their results are combined into a single
diagnosis, working outward through the network stack so a single failure is attributed to its
most likely cause instead of being reported as a generic "internet is down":

1. **Network adapter** - is an adapter present, operational, and actually carrying traffic?
2. **IP address** - does the adapter have a usable IPv4 address (vs. an APIPA `169.254.x.x` address, which means DHCP failed)?
3. **Gateway** - is the default gateway known and reachable? (Races ICMP and TCP - see below.)
4. **Internet (multi-endpoint)** - ICMP pings to `9.9.9.9`, `8.8.8.8`, and `1.1.1.1`. A single unreachable endpoint doesn't mean internet is down; the diagnosis only escalates once none of the three respond.
5. **DNS** - can a hostname (`www.ripe.net` by default) actually be resolved? On failure, also
   cross-checked against public resolvers to see if it's your configured DNS server specifically
   (see Known limitations below).
6. **General HTTPS** - a full DNS → TCP → TLS → HTTP request to a general site, timed at every stage.
7. **Application endpoints** - any HTTPS or TCP/UDP port endpoints you've configured in Settings.
8. **Time synchronization** - how far the system clock has drifted from `pool.ntp.org` (a minimal SNTP client; .NET has no built-in NTP support).

Because a network firewall can block ICMP without blocking real traffic, the diagnosis engine
specifically compares ping results against HTTPS results: if ping fails but HTTPS works, it
reports "internet available, ICMP possibly filtered" rather than claiming the connection is
down.

## Two screens

- **Status** (left-click the tray icon): a simple green/red/amber overview for end users - is
  the network, internet, DNS, and each configured endpoint working?
- **Diagnostics** (right-click → Diagnostics): a technical screen for support staff - every
  probe's raw measurements (latency, resolved IPs, HTTP status codes, TLS timings, exact error
  text), the derived diagnosis, incident history, and diagnostic-log status. Includes a
  "Check now" button and a "Copy diagnostics" button that generates a plain-text report suitable
  for pasting into a support ticket.

## Incidents

A run of consecutive failed diagnosis cycles (debounced, so a brief blip doesn't create noise)
is grouped into a single **incident** with a unique ID (`INC-yyyyMMdd-####`), a classification
(Network / IpConfiguration / Gateway / Internet / Dns / Https / Application / TimeSync /
FirewallSuspected), a start/end time, and a snapshot of every probe's result at the start and at
resolution. Incidents are listed and browsable from the Diagnostics screen, independent of
whatever diagnostic logging level is configured.

## Diagnostic logging

Optional, off by default. Configurable levels in Settings → Diagnostics:

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

Settings → Endpoints lets you add, edit, enable/disable, and remove any number of application
endpoints, of two kinds:

- **HTTPS** - a full DNS → TCP → TLS → HTTP check against a URL, same as the built-in general
  HTTPS check.
- **Port (TCP/UDP)** - plain reachability of a host:port, for services that aren't HTTP (a
  database, an internal TCP service, ...). TCP treats both a successful connect and an actively
  refused connection as "reachable" (either proves something answered); UDP is inherently
  ambiguous, so a plain timeout is reported as "uncertain" rather than a false success/failure.

Each enabled endpoint is checked every cycle exactly like the built-in checks and shown by name
(not raw URL/host) on the simple status screen. See [`config.example.json`](config.example.json)
for the on-disk shape, including an example of adding endpoints for a fictitious API.

## Uptime Kuma heartbeat

Settings → Uptime Kuma lets you configure a push-monitor URL and interval (minimum 60 seconds).
When a URL is set, the app sends a plain periodic `GET` to it - no dependency on any
connectivity check, matching how Uptime Kuma push monitors are normally used. Off by default.
See the [Uptime Kuma project](https://github.com/louislam/uptime-kuma) for what it is and how to
set up a push monitor to get a URL from.

## Project structure

For a new contributor, the call chain from startup to a diagnosis on screen is:

- **`Program.cs`** - entry point. Installs global exception handlers, enforces a single running
  instance (`Startup/SingleInstanceManager.cs`), then hands off to...
- **`UI/TrayApplicationContext.cs`** - the composition root. Owns the tray icon/menu, loads
  `AppSettings`, and constructs everything below.
- **`Network/ConnectivityMonitor.cs`** - a fast (1s) poller driving only the tray icon's
  green/red/amber state. Independent of the diagnosis engine below; no UI dependency.
- **`Network/DiagnosticsCoordinator.cs`** - the "real" diagnostics engine. Every 15 seconds it
  runs every probe under `Network/Probes/` (see `Network/Probes/IProbe.cs` for the
  probe/diagnosis separation of concerns) in parallel, assembles a `ProbeSnapshot`, and hands it
  to...
- **`Network/Diagnosis/DiagnosisEngine.cs`** - a pure function that walks the snapshot bottom-up
  (adapter → IP → gateway → WAN → DNS → HTTPS → application → time-sync) and returns the single
  most likely root cause, rather than reporting every failed probe as its own symptom.
- From there, `Network/Diagnostics/IncidentTracker.cs` turns a run of failing cycles into a
  tracked `Incident` (persisted via `IncidentStore`), `Network/Diagnostics/DiagnosticLogger.cs`
  optionally writes a detailed JSONL log, and `UI/StatusPopupForm.cs` /
  `UI/DiagnosticsForm.cs` render the result - both marshal background-thread events onto the UI
  thread via `SynchronizationContext`, guarding every continuation with an `IsDisposed` check.

All user-facing text (including diagnosis headlines/explanations) goes through
`Localization/LocalizationManager.cs`, loading `Localization/en.json` / `nl.json`; there is no
hardcoded UI language anywhere in the probe/diagnosis layer.

## Building and running

Requires the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`).

```powershell
dotnet build InternetMonitor.sln
```

Publish a single self-contained `.exe` (no .NET runtime needed on the target machine):

```powershell
dotnet publish InternetMonitor/InternetMonitor.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish
```

The result, `publish/InternetMonitor.exe`, can be copied to and run on any Windows 10/11 x64
machine. This intentionally skips `PublishReadyToRun` (which roughly doubles framework assembly
sizes by embedding precompiled native code) to keep the download small - a slightly slower JIT
cold start is an easy trade for a tray app that starts once and runs for days.

## Settings and data locations

- Settings: `%AppData%\InternetMonitor\settings.json`
- Diagnostic log: `%AppData%\InternetMonitor\logs\diagnostics.log` (+ rotated `.log.1`, `.log.2`, ...)
- Incident history: `%AppData%\InternetMonitor\incidents.json`
- Simple outage log: `%AppData%\InternetMonitor\logs\log.txt`

None of these live inside the application directory, so the app itself can be replaced/updated
without touching configuration or history.

## Known limitations

- Gateway reachability races a plain ICMP ping against a TCP connect attempt to a few common
  ports (80/443/53), all at once - whichever answers first wins - so it works both on networks
  where ICMP is fine (the common case) and ones that filter it, without waiting through both in
  sequence. A gateway that answers neither will still read as unreachable, but that combination
  is rare in practice. This check has no retry: a network settings change (even just the DNS
  server) can make the gateway genuinely unreachable at the OS level for a few seconds, and
  that's real, momentary state worth showing, not a measurement error to hide.
- DNS "resolver used" reporting shows the interface's *configured* DNS servers, not necessarily
  which one actually answered a given query - Windows doesn't expose that for a normal lookup.
  When a lookup fails, though, the app cross-checks the same hostname directly against three
  well-known public resolvers (9.9.9.9, 8.8.8.8, 1.1.1.1); if they succeed where the system
  lookup didn't, the diagnosis says so - narrowing the problem down to "your configured DNS
  server specifically" rather than a broader network/DNS issue.
- Application endpoints are entirely user-configured with no target allowlist, by design (this
  is a local, single-user tool) - see [Security notes](#security-notes) below.

## Security notes

- **User-configured targets are trusted.** Application endpoints and the ping target are
  entirely user-supplied and flow directly into raw TCP/UDP/HTTPS probes with no target
  allowlist. This is intended - it's a local, single-user monitoring tool, and you're only ever
  pointing it at hosts you choose. It does mean that importing a `settings.json` you don't trust
  would let it probe whatever hosts that file names.
- **Endpoint URLs can end up in incident/log files.** If a monitored endpoint's URL embeds a
  secret (an API key or webhook token in the query string, for example), that full URL is
  captured verbatim into `incidents.json` and, if detailed diagnostic logging is enabled, into
  `diagnostics.log`. Avoid putting secrets directly in monitored URLs, or keep detailed logging
  off for such endpoints. The Uptime Kuma push URL is handled separately and is never logged.
- **TLS validation is never bypassed.** The HTTPS probe (`Network/Probes/HttpsEndpointProbe.cs`)
  uses .NET's default certificate validation with no custom callback, on every request.
- The app runs unelevated (`asInvoker`, see `app.manifest`) and only ever talks outbound over
  the network - it opens no listening ports and requires no special permissions.

## License

MIT - see [LICENSE](LICENSE).
