using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;

namespace InternetMonitor.Core.Monitors;

/// <summary>What the last delivery did, for the Settings page's status line.</summary>
public sealed record WebhookDeliveryStatus(DateTimeOffset AtUtc, bool Failed, string Detail);

/// <summary>
/// Tells one endpoint when an incident opens or closes: one POST with a small JSON body, which is
/// what Home Assistant, ntfy, Discord, Slack and most automation tools accept as-is. The Kuma push
/// can only say "this computer is alive"; this says what happened.
///
/// An incident on the provider's side is exactly when the internet is down, so the "opened" message
/// usually cannot leave the house. Messages therefore queue: a delivery that fails stays pending,
/// in order, and the host asks for another try as soon as a cycle reaches the internet again - the
/// "opened" and the "resolved" then arrive together, each with its own timestamp. When the internet
/// is fine but the endpoint itself refuses, the retries back off (5, 10, 20, 40 seconds, then every
/// minute) rather than knocking every cycle. Nothing about monitoring waits for any of this.
///
/// Every message carries the incident's UUID next to its readable id, so a receiver can open and
/// close exactly one thing even when two machines post to it.
///
/// The URL must be https, except for an address inside your own network (a private IP, localhost,
/// a bare hostname or a .local/.lan/.home/.internal name), where plain http is allowed: that is
/// where a Home Assistant or ntfy lives that should hear about an outage while it is happening,
/// and that traffic never crosses the internet.
/// </summary>
public sealed class IncidentWebhook : IAsyncDisposable
{
    /// <summary>Messages kept while the endpoint is out of reach; beyond this the oldest is dropped.</summary>
    public const int MaxPending = 50;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly string[] LocalSuffixes = [".local", ".lan", ".home", ".internal", ".home.arpa", ".localhost"];

    private readonly HttpClient _httpClient;
    private readonly string _appVersion;
    private readonly Queue<WebhookPayload> _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private string _url = string.Empty;
    private int _failuresInARow;
    private DateTimeOffset _nextAttemptUtc = DateTimeOffset.MinValue;

    public IncidentWebhook(string appVersion)
        : this(appVersion, new HttpClientHandler())
    {
    }

    /// <summary><paramref name="handler"/> lets a test receive the POST in memory.</summary>
    public IncidentWebhook(string appVersion, HttpMessageHandler handler)
    {
        _appVersion = appVersion;
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("InternetMonitor");
    }

    /// <summary>The most recent delivery, or null before the first one.</summary>
    public WebhookDeliveryStatus? LastDelivery { get; private set; }

    /// <summary>Raised after every delivery attempt, on a thread-pool thread; UI subscribers must marshal.</summary>
    public event Action<WebhookDeliveryStatus>? DeliveryCompleted;

    public bool IsActive => _url.Length > 0;

    /// <summary>Messages waiting for the endpoint to be reachable again.</summary>
    public int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>An absolute https URL anywhere, or an http URL to an address inside the local network.</summary>
    public static bool IsUsableUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLocalHost(uri)));

    /// <summary>
    /// Whether the host is one that never resolves to the public internet: loopback, a private or
    /// link-local address, a bare hostname, or a name under a local-only suffix.
    /// </summary>
    public static bool IsLocalHost(Uri uri)
    {
        if (uri.IsLoopback)
        {
            return true;
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address))
        {
            return IsPrivate(address);
        }

        string host = uri.Host.ToLowerInvariant();
        return !host.Contains('.') || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        }

        byte[] b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Sets the URL; anything unusable switches the webhook off. A new address drops what was queued for the old one.</summary>
    public void Reconfigure(string url)
    {
        string trimmed = url.Trim();
        string usable = IsUsableUrl(trimmed) ? trimmed : string.Empty;
        lock (_lock)
        {
            if (usable != _url)
            {
                _pending.Clear();
            }

            _url = usable;
        }
    }

    /// <summary>An incident opened (<paramref name="opened"/>) or was resolved. Queues the message and returns at once.</summary>
    public void Notify(Incident incident, bool opened)
    {
        if (!IsActive)
        {
            return;
        }

        lock (_lock)
        {
            while (_pending.Count >= MaxPending)
            {
                _pending.Dequeue();
            }

            _pending.Enqueue(Payload(incident, opened ? "opened" : "resolved", _appVersion));
        }

        _ = FlushAsync();
    }

    /// <summary>When the next retry may go: moves out after each failure in a row, back to "now" after a success.</summary>
    public DateTimeOffset NextAttemptUtc => _nextAttemptUtc;

    /// <summary>
    /// The internet is reachable again: try to deliver what is still waiting, unless a recent
    /// failure says to wait a little longer first. Returns at once.
    /// </summary>
    public void RetryPending()
    {
        if (PendingCount > 0 && DateTimeOffset.UtcNow >= _nextAttemptUtc)
        {
            _ = FlushAsync();
        }
    }

    /// <summary>A delivery the user asked for from Settings, to see the endpoint receive something. Not queued: it is about now.</summary>
    public async Task<WebhookDeliveryStatus?> SendTestAsync(CancellationToken cancellationToken)
    {
        if (!IsActive)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DeliverAsync(TestPayload(_appVersion), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The JSON body for one incident event. Public and pure, so the shape is testable and documented by the test.</summary>
    public static WebhookPayload Payload(Incident incident, string eventName, string appVersion) => new(
        eventName,
        incident.ConditionKey is null ? "outage" : "warning",
        incident.Id,
        incident.Uuid.ToString("D"),
        incident.Cause.ToString(),
        incident.ConditionKey is null ? ProviderReportData.SideOf(incident.Cause).ToString() : "NetworkHealth",
        incident.Headline,
        incident.StartUtc,
        incident.EndUtc,
        incident.Duration is { } duration ? (long)duration.TotalSeconds : null,
        appVersion);

    public static WebhookPayload TestPayload(string appVersion) =>
        new("test", "test", "TEST", Guid.NewGuid().ToString("D"), "Healthy", "None", "Test delivery from Internet Monitor", DateTimeOffset.UtcNow, null, null, appVersion);

    /// <summary>
    /// Delivers the queue in order, one at a time, and stops at the first failure so nothing
    /// arrives out of sequence: the message stays at the head of the queue for the next try.
    /// </summary>
    private async Task FlushAsync()
    {
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            // A flush is running; it drains whatever is queued, including what was just added.
            return;
        }

        try
        {
            while (true)
            {
                WebhookPayload payload;
                lock (_lock)
                {
                    if (_pending.Count == 0)
                    {
                        return;
                    }

                    payload = _pending.Peek();
                }

                WebhookDeliveryStatus? status = await DeliverAsync(payload, CancellationToken.None).ConfigureAwait(false);
                if (status is null || status.Failed)
                {
                    // 5, 10, 20, 40 seconds, then once a minute - a refusing endpoint is not knocked
                    // on every cycle, and a new message still tries at once (Notify bypasses this).
                    int failures = Math.Min(++_failuresInARow, 5);
                    _nextAttemptUtc = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Min(60, 5 * (1 << (failures - 1))));
                    return;
                }

                _failuresInARow = 0;
                _nextAttemptUtc = DateTimeOffset.MinValue;
                lock (_lock)
                {
                    if (_pending.Count > 0 && ReferenceEquals(_pending.Peek(), payload))
                    {
                        _pending.Dequeue();
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WebhookDeliveryStatus?> DeliverAsync(WebhookPayload payload, CancellationToken cancellationToken)
    {
        string url = _url;
        if (url.Length == 0)
        {
            return null;
        }

        WebhookDeliveryStatus status;
        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(url, payload, Json, cancellationToken).ConfigureAwait(false);
            status = new WebhookDeliveryStatus(DateTimeOffset.UtcNow, !response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or InvalidOperationException or NotSupportedException)
        {
            status = new WebhookDeliveryStatus(DateTimeOffset.UtcNow, true, ex is OperationCanceledException ? "timed out" : ex.Message);
        }

        LastDelivery = status;
        DeliveryCompleted?.Invoke(status);
        return status;
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The body of a webhook POST. Serialised in camelCase; nulls are left out.</summary>
public sealed record WebhookPayload(
    string Event,
    string Kind,
    string Id,
    string Uuid,
    string Cause,
    string Side,
    string Headline,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long? DurationSeconds,
    string AppVersion);
