using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Localization;

/// <summary>Optional text: a help balloon exists only when a language has it, and never shows a bracketed key.</summary>
[Collection("Localization")]
public sealed class TryGetTests
{
    [Fact]
    public void An_existing_key_gives_its_text_and_a_missing_one_gives_null()
    {
        LocalizationManager loc = LocalizationManager.Instance;

        Assert.Equal(loc.Get("settings.language"), loc.TryGet("settings.language"));
        Assert.NotNull(loc.TryGet("settings.language.help"));
        Assert.Null(loc.TryGet("settings.currentVersion.help"));
        Assert.Null(loc.TryGet("no.such.key"));
        Assert.DoesNotContain("[", loc.TryGet("settings.latencyWarning.help"));
    }
}
