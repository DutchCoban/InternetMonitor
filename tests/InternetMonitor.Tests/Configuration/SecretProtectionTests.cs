using InternetMonitor.Configuration;
using Xunit;

namespace InternetMonitor.Tests.Configuration;

/// <summary>
/// Credentials in settings.json: protected values round-trip through DPAPI, a readable value is
/// recognised as such and both kinds load as plain text, the file only holds protected values when
/// the user asked for that, and the settings backup stays readable.
/// </summary>
[Collection("Localization")]
public sealed class SecretProtectionTests
{
    [Fact]
    public void A_protected_value_reads_back_and_is_recognisable_as_protected()
    {
        string protectedValue = SecretProtection.Protect("https://hooks.example/T0K3N");

        Assert.StartsWith(SecretProtection.Prefix, protectedValue);
        Assert.True(SecretProtection.IsProtected(protectedValue));
        Assert.DoesNotContain("T0K3N", protectedValue);
        Assert.Equal("https://hooks.example/T0K3N", SecretProtection.Unprotect(protectedValue));

        // Protecting twice changes nothing; an empty value stays empty.
        Assert.Equal(protectedValue, SecretProtection.Protect(protectedValue));
        Assert.Equal(string.Empty, SecretProtection.Protect(string.Empty));
    }

    [Fact]
    public void A_readable_value_is_returned_as_is_and_a_damaged_protected_one_as_null()
    {
        Assert.False(SecretProtection.IsProtected("https://hooks.example/plain"));
        Assert.Equal("https://hooks.example/plain", SecretProtection.Unprotect("https://hooks.example/plain"));
        Assert.Null(SecretProtection.Unprotect(SecretProtection.Prefix + "not base64 at all"));
        Assert.Null(SecretProtection.Unprotect(SecretProtection.Prefix + Convert.ToBase64String([1, 2, 3, 4])));
    }

    [Fact]
    public void With_protection_on_the_file_holds_no_readable_credential_and_loads_back_plain()
    {
        var settings = new AppSettings
        {
            ProtectSecrets = true,
            WebhookUrl = "https://hooks.example/WEBHOOK-SECRET",
            KumaPushUrl = "https://kuma.example/api/push/KUMA-SECRET",
            ApplicationEndpoints = [new EndpointConfig("api", "API", EndpointKind.Url, "https://api.example/health", Headers: [new EndpointHeader("X-Api-Key", "HEADER-SECRET")])],
        };

        Assert.True(settings.Save());
        string json = File.ReadAllText(AppPaths.SettingsFile);
        Assert.DoesNotContain("WEBHOOK-SECRET", json);
        Assert.DoesNotContain("KUMA-SECRET", json);
        Assert.DoesNotContain("HEADER-SECRET", json);
        Assert.Contains("\"dpapi:", json);

        // The object itself keeps the plain values - nothing else in the app knows about protection.
        Assert.Equal("https://hooks.example/WEBHOOK-SECRET", settings.WebhookUrl);

        AppSettings loaded = AppSettings.Load();
        Assert.True(loaded.ProtectSecrets);
        Assert.False(loaded.LoadedPlaintextSecrets);
        Assert.Equal("https://hooks.example/WEBHOOK-SECRET", loaded.WebhookUrl);
        Assert.Equal("https://kuma.example/api/push/KUMA-SECRET", loaded.KumaPushUrl);
        Assert.Equal("HEADER-SECRET", loaded.ApplicationEndpoints[0].Headers[0].Value);
    }

    [Fact]
    public void A_file_with_one_protected_and_one_readable_credential_loads_both_and_reports_the_readable_one()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, $$"""
            { "ProtectSecrets": true,
              "WebhookUrl": "{{SecretProtection.Protect("https://hooks.example/protected")}}",
              "KumaPushUrl": "https://kuma.example/api/push/typed-by-hand" }
            """);

        AppSettings loaded = AppSettings.Load();

        Assert.Equal("https://hooks.example/protected", loaded.WebhookUrl);
        Assert.Equal("https://kuma.example/api/push/typed-by-hand", loaded.KumaPushUrl);
        Assert.True(loaded.LoadedPlaintextSecrets);
        Assert.True(loaded.HasSecrets);

        // Saving again - what the start-up check does for a user who chose protection - protects the hand-typed one too.
        Assert.True(loaded.Save());
        Assert.DoesNotContain("typed-by-hand", File.ReadAllText(AppPaths.SettingsFile));
    }

    [Fact]
    public void Without_a_choice_or_with_protection_off_the_file_stays_readable()
    {
        var undecided = new AppSettings { WebhookUrl = "https://hooks.example/readable" };
        Assert.True(undecided.Save());
        Assert.Contains("https://hooks.example/readable", File.ReadAllText(AppPaths.SettingsFile));
        Assert.Null(AppSettings.Load().ProtectSecrets);
        Assert.True(AppSettings.Load().LoadedPlaintextSecrets);

        var declined = new AppSettings { ProtectSecrets = false, WebhookUrl = "https://hooks.example/readable" };
        Assert.True(declined.Save());
        Assert.Contains("https://hooks.example/readable", File.ReadAllText(AppPaths.SettingsFile));
        Assert.False(AppSettings.Load().ProtectSecrets);
    }

    [Fact]
    public void The_settings_backup_is_always_readable_so_it_restores_elsewhere()
    {
        string path = Path.Combine(Path.GetTempPath(), $"im-backup-{Guid.NewGuid():N}.json");
        try
        {
            new AppSettings { ProtectSecrets = true, WebhookUrl = "https://hooks.example/BACKUP-SECRET" }.ExportTo(path);
            Assert.Contains("BACKUP-SECRET", File.ReadAllText(path));
            Assert.Equal("https://hooks.example/BACKUP-SECRET", AppSettings.ImportFrom(path)!.WebhookUrl);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
