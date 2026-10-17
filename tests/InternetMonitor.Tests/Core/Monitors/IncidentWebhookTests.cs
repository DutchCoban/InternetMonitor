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
