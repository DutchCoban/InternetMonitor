using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Reports;

namespace InternetMonitor.Core.Monitors;

/// <summary>What the last delivery did, for the Settings page's status line.</summary>
public sealed record WebhookDeliveryStatus(DateTimeOffset AtUtc, bool Failed, string Detail);

/// <summary>
/// Tells one HTTPS endpoint when an incident opens or closes: one POST with a small JSON body,
/// which is what Home Assistant, ntfy, Discord, Slack and most automation tools accept as-is. The
/// Kuma push can only say "this computer is alive"; this says what happened.
///
/// Fire-and-forget by design, like every other integration here: a delivery that fails is noted
/// (<see cref="LastDelivery"/>) and never retried, and nothing about monitoring waits for it. The
/// URL must be https, the same rule as the Kuma push URL and for the same reason - whatever is in
/// the address or the body is not for the network to read.
/// </summary>
public sealed class IncidentWebhook : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly string _appVersion;
    private string _url = string.Empty;

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

    /// <summary>Raised after every delivery, on a thread-pool thread; UI subscribers must marshal.</summary>
    public event Action<WebhookDeliveryStatus>? DeliveryCompleted;

    public bool IsActive => _url.Length > 0;

    public static bool IsUsableUrl(string url) => KumaPusher.IsUsableUrl(url);

    /// <summary>Sets the URL; anything that is not an absolute https address switches the webhook off.</summary>
    public void Reconfigure(string url)
    {
        string trimmed = url.Trim();
        _url = IsUsableUrl(trimmed) ? trimmed : string.Empty;
    }

    /// <summary>An incident opened (<paramref name="opened"/>) or was resolved. Returns at once.</summary>
    public void Notify(Incident incident, bool opened)
    {
        if (!IsActive)
        {
            return;
        }

        _ = DeliverAsync(Payload(incident, opened ? "opened" : "resolved", _appVersion), CancellationToken.None);
    }

    /// <summary>A delivery the user asked for from Settings, to see the endpoint receive something.</summary>
    public Task<WebhookDeliveryStatus?> SendTestAsync(CancellationToken cancellationToken) =>
        IsActive ? DeliverAsync(TestPayload(_appVersion), cancellationToken) : Task.FromResult<WebhookDeliveryStatus?>(null);

    /// <summary>The JSON body for one incident event. Public and pure, so the shape is testable and documented by the test.</summary>
    public static WebhookPayload Payload(Incident incident, string eventName, string appVersion) => new(
        eventName,
        incident.ConditionKey is null ? "outage" : "warning",
        incident.Id,
        incident.Cause.ToString(),
        incident.ConditionKey is null ? ProviderReportData.SideOf(incident.Cause).ToString() : "NetworkHealth",
        incident.Headline,
        incident.StartUtc,
        incident.EndUtc,
        incident.Duration is { } duration ? (long)duration.TotalSeconds : null,
        appVersion);

    public static WebhookPayload TestPayload(string appVersion) =>
        new("test", "test", "TEST", "Healthy", "None", "Test delivery from Internet Monitor", DateTimeOffset.UtcNow, null, null, appVersion);

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
        return ValueTask.CompletedTask;
    }
}

/// <summary>The body of a webhook POST. Serialised in camelCase; nulls are left out.</summary>
public sealed record WebhookPayload(
    string Event,
    string Kind,
    string Id,
    string Cause,
    string Side,
    string Headline,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long? DurationSeconds,
    string AppVersion);
