using System.Net;
using System.Text.Json;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Monitors;
using Xunit;

namespace InternetMonitor.Tests.Core.Monitors;

/// <summary>The webhook's body is a contract other tools are configured against, so its shape is pinned here.</summary>
public sealed class IncidentWebhookTests
{
    private static Incident Outage() => new()
    {
        Id = "INC-20261017-0003",
        Cause = RootCause.Internet,
        StartUtc = new DateTimeOffset(2026, 10, 17, 10, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 10, 17, 10, 4, 30, TimeSpan.Zero),
        Status = IncidentStatus.Resolved,
        Headline = "No public address reachable",
    };

    [Fact]
    public void The_payload_names_the_event_the_side_and_the_duration()
    {
        WebhookPayload payload = IncidentWebhook.Payload(Outage(), "resolved", "2.3.0");

        Assert.Equal("resolved", payload.Event);
        Assert.Equal("outage", payload.Kind);
        Assert.Equal("INC-20261017-0003", payload.Id);
        Assert.True(Guid.TryParseExact(payload.Uuid, "D", out Guid uuid) && uuid != Guid.Empty);
        Assert.Null(payload.Label);
        Assert.Equal("Internet", payload.Cause);
        Assert.Equal("Provider", payload.Side);
        Assert.Equal(270, payload.DurationSeconds);
        Assert.Equal("2.3.0", payload.AppVersion);
    }

    [Fact]
    public void A_network_health_warning_is_a_warning_not_an_outage()
    {
        var warning = new Incident { Id = "INC-1", Cause = RootCause.NetworkHealth, StartUtc = DateTimeOffset.UtcNow, Headline = "x", ConditionKey = "packetLoss" };

        WebhookPayload payload = IncidentWebhook.Payload(warning, "opened", "2.3.0");

        Assert.Equal("warning", payload.Kind);
        Assert.Null(payload.EndUtc);
        Assert.Null(payload.DurationSeconds);
    }

    [Fact]
    public void A_network_change_is_a_change_on_the_side_it_is_about()
    {
        var move = new Incident { Id = "INC-2", Cause = RootCause.NetworkChange, StartUtc = DateTimeOffset.UtcNow, Headline = "Connection moved from Ethernet to Wi-Fi", ConditionKey = NetworkChangeMonitor.PathChanged };
        var apipa = new Incident { Id = "INC-3", Cause = RootCause.NetworkChange, StartUtc = DateTimeOffset.UtcNow, Headline = "x", ConditionKey = NetworkChangeMonitor.Apipa };

        Assert.Equal(("change", "ThisComputer"), (IncidentWebhook.Payload(move, "opened", "2.5.0").Kind, IncidentWebhook.Payload(move, "opened", "2.5.0").Side));
        Assert.Equal(("change", "HomeNetwork"), (IncidentWebhook.Payload(apipa, "opened", "2.5.0").Kind, IncidentWebhook.Payload(apipa, "opened", "2.5.0").Side));
    }

    [Fact]
    public async Task A_delivery_posts_camel_case_json_to_the_configured_https_url()
    {
        var handler = new CapturingHandler();
        await using var webhook = new IncidentWebhook("2.3.0", handler);
        webhook.Reconfigure("https://hooks.example/incident");

        WebhookDeliveryStatus? status = await webhook.SendTestAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.False(status.Failed);
        Assert.Equal("https://hooks.example/incident", handler.Url);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("test", body.RootElement.GetProperty("event").GetString());
        Assert.Equal("2.3.0", body.RootElement.GetProperty("appVersion").GetString());
        Assert.False(body.RootElement.TryGetProperty("endUtc", out _));
        Assert.False(body.RootElement.TryGetProperty("label", out _));
    }

    [Fact]
    public async Task The_label_the_user_chose_names_the_machine_and_nothing_else_does()
    {
        var handler = new CapturingHandler();
        await using var webhook = new IncidentWebhook("2.3.0", handler);
        webhook.Reconfigure("https://hooks.example/incident", "  Balie Utrecht ");

        await webhook.SendTestAsync(CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Balie Utrecht", body.RootElement.GetProperty("label").GetString());
        Assert.DoesNotContain(Environment.MachineName, handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://192.168.1.20:8123/api/webhook/x")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://172.20.3.4/hook")]
    [InlineData("http://homeassistant.local:8123/api/webhook/x")]
    [InlineData("http://nas/hook")]
    [InlineData("http://localhost:8080/hook")]
    [InlineData("http://[fd12::1]/hook")]
    public void Plain_http_is_allowed_inside_the_local_network(string url) => Assert.True(IncidentWebhook.IsUsableUrl(url));

    [Theory]
    [InlineData("http://8.8.8.8/hook")]
    [InlineData("http://172.32.0.1/hook")]
    [InlineData("http://hooks.example.com/incident")]
    public void Plain_http_to_the_internet_is_refused(string url) => Assert.False(IncidentWebhook.IsUsableUrl(url));

    [Fact]
    public async Task A_failed_delivery_waits_and_goes_in_order_once_the_endpoint_answers()
    {
        var handler = new FlakyHandler { Failing = true };
        await using var webhook = new IncidentWebhook("2.3.0", handler);
        webhook.Reconfigure("https://hooks.example/incident");

        webhook.Notify(Outage(), opened: true);
        await handler.WaitForAttemptsAsync(1);
        Assert.Equal(1, webhook.PendingCount);
        Assert.True(webhook.LastDelivery!.Failed);

        // The failure set a back-off: a retry right now is not attempted.
        Assert.True(webhook.NextAttemptUtc > DateTimeOffset.UtcNow);
        webhook.RetryPending();
        await Task.Delay(50);
        Assert.Equal(1, handler.Attempts);

        handler.Failing = false;
        webhook.Notify(Outage(), opened: false);
        await handler.WaitForAttemptsAsync(3);
        await Task.Delay(50);

        Assert.Equal(0, webhook.PendingCount);
        Assert.Equal(["opened", "resolved"], handler.DeliveredEvents);
        Assert.Equal(DateTimeOffset.MinValue, webhook.NextAttemptUtc);
    }

    [Fact]
    public void The_same_incident_opens_and_closes_under_one_uuid_and_two_incidents_never_share_one()
    {
        Incident incident = Outage();
        var other = new Incident { Id = incident.Id, Cause = incident.Cause, StartUtc = incident.StartUtc, Headline = incident.Headline };

        Assert.Equal(IncidentWebhook.Payload(incident, "opened", "2.3.0").Uuid, IncidentWebhook.Payload(incident, "resolved", "2.3.0").Uuid);
        Assert.NotEqual(IncidentWebhook.Payload(incident, "opened", "2.3.0").Uuid, IncidentWebhook.Payload(other, "opened", "2.3.0").Uuid);
    }

    [Theory]
    [InlineData("http://hooks.example/incident")]
    [InlineData("hooks.example")]
    [InlineData("")]
    public async Task Anything_but_an_https_url_switches_the_webhook_off(string url)
    {
        var handler = new CapturingHandler();
        await using var webhook = new IncidentWebhook("2.3.0", handler);
        webhook.Reconfigure(url);

        webhook.Notify(Outage(), opened: true);
        Assert.False(webhook.IsActive);
        Assert.Null(await webhook.SendTestAsync(CancellationToken.None));
        Assert.Null(handler.Url);
    }

    /// <summary>Refuses every POST while <see cref="Failing"/>, and records the events it accepted.</summary>
    private sealed class FlakyHandler : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public bool Failing { get; set; }

        public List<string> DeliveredEvents { get; } = [];

        public async Task WaitForAttemptsAsync(int count)
        {
            for (int i = 0; i < 200 && Volatile.Read(ref _attempts) < count; i++)
            {
                await Task.Delay(10);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            if (Failing)
            {
                throw new HttpRequestException("no route to host");
            }

            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using JsonDocument json = JsonDocument.Parse(body);
            lock (DeliveredEvents)
            {
                DeliveredEvents.Add(json.RootElement.GetProperty("event").GetString()!);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
