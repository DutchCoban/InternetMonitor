using System.Text.Json;
using InternetMonitor.Configuration;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using Xunit;

namespace InternetMonitor.Tests.Core.Diagnosis;

/// <summary>
/// The diagnostics log records incidents opening and closing as one JSON line each, stays silent
/// when logging is off, falls back to the app's own logs folder, and rotates by size with a fixed
/// number of numbered backups.
/// </summary>
public sealed class DiagnosticLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"im-log-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Incident Outage() => new()
    {
        Id = "INC-20261017-190000.000",
        Cause = RootCause.Internet,
        StartUtc = new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero),
        EndUtc = new DateTimeOffset(2026, 10, 17, 19, 4, 30, TimeSpan.Zero),
        Status = IncidentStatus.Resolved,
        Headline = "No public address reachable",
    };

    [Fact]
    public void An_incident_opening_and_closing_is_one_json_line_each()
    {
        var logger = new DiagnosticLogger(new AppSettings { DiagnosticLogLevel = DiagnosticLogLevel.Basic, DiagnosticLogDirectory = _directory });

        logger.LogIncidentOpened(Outage());
        logger.LogIncidentResolved(Outage());

        string[] lines = File.ReadAllLines(logger.LogPath);
        Assert.Equal(2, lines.Length);
        using JsonDocument opened = JsonDocument.Parse(lines[0]);
        Assert.Equal("incidentOpened", opened.RootElement.GetProperty("type").GetString());
        Assert.Equal("INC-20261017-190000.000", opened.RootElement.GetProperty("id").GetString());
        Assert.Equal("Internet", opened.RootElement.GetProperty("cause").GetString());
        using JsonDocument resolved = JsonDocument.Parse(lines[1]);
        Assert.Equal("incidentResolved", resolved.RootElement.GetProperty("type").GetString());
        Assert.Equal(270, resolved.RootElement.GetProperty("durationSeconds").GetDouble());
    }

    [Fact]
    public void Nothing_is_written_when_logging_is_off()
    {
        var logger = new DiagnosticLogger(new AppSettings { DiagnosticLogLevel = DiagnosticLogLevel.Off, DiagnosticLogDirectory = _directory });

        logger.LogIncidentOpened(Outage());
        logger.LogIncidentResolved(Outage());

        Assert.False(File.Exists(logger.LogPath));
    }

    [Fact]
    public void An_empty_directory_setting_means_the_apps_own_logs_folder()
    {
        var logger = new DiagnosticLogger(new AppSettings { DiagnosticLogDirectory = string.Empty });

        Assert.Equal(AppPaths.LogsDir, logger.ResolvedDirectory());
        Assert.StartsWith(TestSetup.DataDirectory, logger.LogPath);
    }

    [Fact]
    public void The_log_rotates_by_size_into_numbered_backups_and_drops_the_oldest()
    {
        var settings = new AppSettings
        {
            DiagnosticLogLevel = DiagnosticLogLevel.Basic,
            DiagnosticLogDirectory = _directory,
            MaxDiagnosticLogFileSizeBytes = 400,
            MaxDiagnosticLogFiles = 2,
        };
        var logger = new DiagnosticLogger(settings);

        // Each line is well over 100 bytes, so every few lines the file passes the limit and rotates.
        for (int i = 0; i < 40; i++)
        {
            logger.LogIncidentOpened(Outage());
        }

        Assert.True(File.Exists(logger.LogPath));
        Assert.True(File.Exists(logger.LogPath + ".1"));
        Assert.True(File.Exists(logger.LogPath + ".2"));
        Assert.False(File.Exists(logger.LogPath + ".3"));
        Assert.True(new FileInfo(logger.LogPath + ".1").Length >= 400);
    }
}
