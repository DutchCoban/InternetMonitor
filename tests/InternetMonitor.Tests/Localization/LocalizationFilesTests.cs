using System.Reflection;
using System.Text.Json;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Localization;

/// <summary>
/// The eight embedded language files are one table: every language has exactly English's keys,
/// in English's order, and no value is empty. A key added to one file and not the others would
/// otherwise only show up as "[key]" on a screen in that language.
/// </summary>
public sealed class LocalizationFilesTests
{
    private static List<(string Key, string Value)> Read(string code)
    {
        Assembly app = typeof(LocalizationManager).Assembly;
        using Stream stream = app.GetManifestResourceStream($"InternetMonitor.Localization.{code}.json")
            ?? throw new InvalidOperationException($"{code}.json is not embedded");
        using JsonDocument json = JsonDocument.Parse(stream);
        return [.. json.RootElement.EnumerateObject().Select(p => (p.Name, p.Value.GetString() ?? string.Empty))];
    }

    [Fact]
    public void Every_language_has_the_same_keys_in_the_same_order_as_english()
    {
        List<(string Key, string Value)> english = Read("en");
        Assert.NotEmpty(english);
        Assert.Equal(english.Count, english.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());

        foreach ((string code, _) in LocalizationManager.SupportedLanguages)
        {
            List<(string Key, string Value)> language = Read(code);
            Assert.True(english.Select(e => e.Key).SequenceEqual(language.Select(l => l.Key)), $"{code}.json does not have English's keys in English's order");
            Assert.DoesNotContain(language, l => l.Value.Length == 0);
        }
    }

    [Fact]
    public void Every_language_keeps_the_placeholders_of_the_english_text()
    {
        Dictionary<string, string> english = Read("en").ToDictionary(e => e.Key, e => e.Value);

        foreach ((string code, _) in LocalizationManager.SupportedLanguages)
        {
            foreach ((string key, string value) in Read(code))
            {
                IEnumerable<string> expected = Placeholders(english[key]);
                Assert.True(expected.OrderBy(p => p).SequenceEqual(Placeholders(value).OrderBy(p => p)), $"{code}.json: {key} has different placeholders than English");
            }
        }
    }

    private static IEnumerable<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct();
}
