using System.Net;
using InternetMonitor.Core.Diagnosis;
using InternetMonitor.Core.Incidents;
using InternetMonitor.Core.Steps;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Core.Incidents;

/// <summary>
/// Confirms that text about to be permanently persisted (an incident's headline, explanation,
/// step snapshots) resolves in English even while the screen is showing a different language -
/// the fix for a real bug where a user who switched to Polish mid-session found old incidents'
/// step details frozen in whatever language was active when they happened.
///
/// Every test here explicitly switches <see cref="LocalizationManager.Instance"/> to a non-English
/// language first and restores it to English (this assembly's default, set once in
/// <see cref="TestSetup"/>) in a finally block - these tests mutate genuinely shared, static state,
/// so leaving it switched would be a trap for whichever test runs next.
/// </summary>
[Collection("Localization")]
public sealed class EnglishRecordingTests
{
    [Fact]
    public void GetEnglish_and_FormatEnglish_ignore_the_active_language()
    {
        LocalizationManager.Instance.SetLanguage("nl");
        try
        {
            Assert.NotEqual(LocalizationManager.Instance.Get("cause.healthy"), LocalizationManager.GetEnglish("cause.healthy"));
        }
        finally
        {
            LocalizationManager.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void StepResult_EnglishHeadline_uses_the_key_not_the_live_language()
    {
        LocalizationManager.Instance.SetLanguage("nl");
        try
        {
            string liveHeadline = LocalizationManager.Instance.Format("step.adapter.ok", "Wi-Fi");
            var result = new StepResult(
                StepId.Adapter,
                StepState.Ok,
                liveHeadline,
                null,
                1,
                TimeSpan.Zero,
                DateTimeOffset.UtcNow,
                [],
                null)
            {
                HeadlineKey = "step.adapter.ok",
                HeadlineArgs = ["Wi-Fi"],
            };

            string english = result.EnglishHeadline();

            Assert.Equal(LocalizationManager.FormatEnglish("step.adapter.ok", "Wi-Fi"), english);
            Assert.NotEqual(liveHeadline, english);
        }
        finally
        {
            LocalizationManager.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void StepResult_EnglishHeadline_falls_back_to_the_live_text_when_no_key_was_recorded()
    {
        var result = new StepResult(
            StepId.Adapter, StepState.Ok, "some unresolved live headline", null, 1,
            TimeSpan.Zero, DateTimeOffset.UtcNow, [], null);

        Assert.Equal("some unresolved live headline", result.EnglishHeadline());
    }

    [Fact]
    public void StepSnapshotEntry_From_freezes_English_text_regardless_of_active_language()
    {
        LocalizationManager.Instance.SetLanguage("nl");
        try
        {
            var result = new StepResult(
                StepId.Adapter,
                StepState.Failed,
                LocalizationManager.Instance.Format("step.adapter.disabled", "Ethernet"),
                LocalizationManager.Instance.Get("step.adapter.disabled.detail"),
                0,
                TimeSpan.Zero,
                DateTimeOffset.UtcNow,
                [],
                null)
            {
                HeadlineKey = "step.adapter.disabled",
                HeadlineArgs = ["Ethernet"],
                DetailKey = "step.adapter.disabled.detail",
            };

            StepSnapshotEntry frozen = StepSnapshotEntry.From(result);

            Assert.Equal(LocalizationManager.FormatEnglish("step.adapter.disabled", "Ethernet"), frozen.Headline);
            Assert.Equal(LocalizationManager.GetEnglish("step.adapter.disabled.detail"), frozen.Detail);
            Assert.NotEqual(result.Headline, frozen.Headline);
        }
        finally
        {
            LocalizationManager.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void PlainLanguage_DescribeEnglish_ignores_the_active_language()
    {
        LocalizationManager.Instance.SetLanguage("nl");
        try
        {
            var gatewayResult = new StepResult(
                StepId.Gateway, StepState.Failed, "onbereikbaar", null, null,
                TimeSpan.Zero, DateTimeOffset.UtcNow, [],
                new GatewayFacts(IPAddress.Parse("192.168.1.1"), false, null, null, null));

            var snapshot = new StepSnapshot([gatewayResult], DateTimeOffset.UtcNow);

            Explanation dutch = PlainLanguage.Describe(RootCause.Gateway, snapshot);
            Explanation english = PlainLanguage.DescribeEnglish(RootCause.Gateway, snapshot);

            Assert.NotEqual(dutch.What, english.What);
            Assert.Equal(LocalizationManager.GetEnglish("explain.gateway.what"), english.What);
        }
        finally
        {
            LocalizationManager.Instance.SetLanguage("en");
        }
    }

    [Fact]
    public void EnvironmentChange_DescribeEnglish_ignores_the_active_language()
    {
        LocalizationManager.Instance.SetLanguage("nl");
        try
        {
            var change = new EnvironmentChange(ChangeKind.Gateway, "192.168.1.1", "192.168.1.254", DateTimeOffset.UtcNow);

            string dutch = change.Describe();
            string english = change.DescribeEnglish();

            Assert.NotEqual(dutch, english);
            Assert.Equal(LocalizationManager.FormatEnglish("change.gateway", "192.168.1.1", "192.168.1.254"), english);
        }
        finally
        {
            LocalizationManager.Instance.SetLanguage("en");
        }
    }
}
