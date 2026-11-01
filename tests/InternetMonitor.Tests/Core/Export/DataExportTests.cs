using System.IO.Compression;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Export;
using Xunit;

namespace InternetMonitor.Tests.Core.Export;

/// <summary>The "export all data" zip carries the settings without any credential: the two URLs that are tokens, header values, and whatever an endpoint URL carries in its query string.</summary>
public sealed class DataExportTests
{
    [Fact]
    public void The_settings_in_the_zip_hold_no_credential()
    {
        var settings = new AppSettings
        {
            WebhookUrl = "https://hooks.example/services/WEBHOOK-SECRET",
            KumaPushUrl = "https://kuma.example/api/push/KUMA-SECRET?status=up",
            WebhookLabel = "Desk 4",
            ApplicationEndpoints =
            [
                new EndpointConfig("api", "API", EndpointKind.Url, "https://api.example/v1/health?api_key=QUERY-SECRET", Headers: [new EndpointHeader("Authorization", "Bearer HEADER-SECRET")]),
            ],
        };

        byte[] zipBytes = DataExportBuilder.Build(settings, [], new Dictionary<string, IReadOnlyList<InternetMonitor.Core.History.MetricPoint>>(), null);

        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
        string json = reader.ReadToEnd();

        Assert.DoesNotContain("WEBHOOK-SECRET", json);
        Assert.DoesNotContain("KUMA-SECRET", json);
        Assert.DoesNotContain("HEADER-SECRET", json);
        Assert.DoesNotContain("QUERY-SECRET", json);
        Assert.Contains("https://api.example/v1/health?", json);
        Assert.Contains("Desk 4", json);
        Assert.Contains("\"Authorization\"", json);
    }
}
