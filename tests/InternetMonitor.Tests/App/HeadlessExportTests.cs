using InternetMonitor.App;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using Xunit;

namespace InternetMonitor.Tests.App;

/// <summary>
/// The command line's --export writes the same documents the Reports page does, from the stored
/// data, and refuses what it cannot do with exit code 1 - what a scheduled task relies on.
/// </summary>
[Collection("Localization")]
public sealed class HeadlessExportTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), $"im-export-{Guid.NewGuid():N}");

    public HeadlessExportTests()
    {
        Directory.CreateDirectory(_out);
        // English, so the shared localisation instance this re-initialises stays what every other test expects.
        new AppSettings { Language = "en", AiExportDetail = InternetMonitor.Core.Export.AiExportDetail.Standard }.Save();
        var store = new IncidentStore();
        store.Clear();
        store.Add(new Incident
        {
            Id = "INC-20261017-190000.000",
            Cause = RootCause.Internet,
            StartUtc = DateTimeOffset.UtcNow.AddHours(-1),
            EndUtc = DateTimeOffset.UtcNow.AddMinutes(-55),
            Status = IncidentStatus.Resolved,
            Headline = "No public address reachable",
        });
    }

    public void Dispose()
    {
        new IncidentStore().Clear();
        Directory.Delete(_out, recursive: true);
    }

    [Fact]
    public async Task The_ai_export_takes_the_detail_level_from_the_argument_or_the_settings()
    {
        string compact = Path.Combine(_out, "compact.md");
        string standard = Path.Combine(_out, "standard.md");

        Assert.Equal(0, await HeadlessExport.RunAsync("ai", compact, "compact"));
        Assert.Equal(0, await HeadlessExport.RunAsync("ai", standard));

        string compactText = File.ReadAllText(compact);
        Assert.Contains("## Instructions for the AI", compactText);
        Assert.Contains("- Detail level: compact", compactText);
        Assert.Contains("### INC-20261017-190000.000", compactText);
        Assert.Contains("- Detail level: standard", File.ReadAllText(standard));
    }

    [Fact]
    public async Task Plain_report_and_zip_each_write_their_own_kind_of_file()
    {
        string plain = Path.Combine(_out, "plain.md");
        string report = Path.Combine(_out, "report.html");
        string zip = Path.Combine(_out, "all.zip");

        Assert.Equal(0, await HeadlessExport.RunAsync("plain", plain));
        Assert.Equal(0, await HeadlessExport.RunAsync("report", report));
        Assert.Equal(0, await HeadlessExport.RunAsync("zip", zip));

        Assert.DoesNotContain("## Instructions for the AI", File.ReadAllText(plain));
        Assert.Contains("<html", File.ReadAllText(report), StringComparison.OrdinalIgnoreCase);
        byte[] bytes = File.ReadAllBytes(zip);
        Assert.Equal((byte)'P', bytes[0]);
        Assert.Equal((byte)'K', bytes[1]);
    }

    [Fact]
    public async Task Bad_requests_fail_with_exit_code_1_and_write_nothing()
    {
        string target = Path.Combine(_out, "never.md");

        Assert.Equal(1, await HeadlessExport.RunAsync("pdf", target));
        Assert.Equal(1, await HeadlessExport.RunAsync("ai", target, "enormous"));
        Assert.Equal(1, await HeadlessExport.RunAsync("ai", Path.Combine(_out, "missing-folder", "x", "never.md")));

        Assert.False(File.Exists(target));
    }
}
