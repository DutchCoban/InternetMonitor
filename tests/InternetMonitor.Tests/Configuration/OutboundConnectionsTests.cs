using InternetMonitor.Configuration;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

/// <summary>
/// The list of parties the app talks to is one list: every row has its two texts, every switch
/// names a real setting, and the README's table is exactly what the list generates - so a new
/// network path without a row, or a README that fell behind, fails here.
/// </summary>
[Collection("Localization")]
public sealed class OutboundConnectionsTests
{
    private const string Start = "<!-- connections:start -->";
    private const string End = "<!-- connections:end -->";

    [Fact]
    public void Every_connection_has_its_texts_and_its_switch_names_a_setting()
    {
        LocalizationManager loc = LocalizationManager.Instance;
        foreach (OutboundConnection connection in OutboundConnections.All)
        {
            Assert.NotNull(loc.TryGet($"connections.{connection.Id}.name"));
            Assert.NotNull(loc.TryGet($"connections.{connection.Id}.sent"));
            if (connection.SettingKey is { } key)
            {
                Assert.NotNull(loc.TryGet(key));
            }

            Assert.False(string.IsNullOrWhiteSpace(connection.Host(new AppSettings())), connection.Id);
        }

        Assert.Equal(OutboundConnections.All.Count, OutboundConnections.All.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public void The_readme_table_is_what_the_list_generates()
    {
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md")).Replace("\r\n", "\n");
        int start = readme.IndexOf(Start, StringComparison.Ordinal);
        int end = readme.IndexOf(End, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "README.md has no connections markers");

        string expected = OutboundConnections.ToMarkdown(new AppSettings(), key => LocalizationManager.GetEnglish(key));
        string actual = readme[(start + Start.Length)..end].Trim('\n') + "\n";

        Assert.True(expected == actual, "README.md's connections table is out of date. Paste this between the markers:\n\n" + expected);
    }

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "InternetMonitor.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("InternetMonitor.sln not found above " + AppContext.BaseDirectory);
    }
}
