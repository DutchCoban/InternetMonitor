using InternetMonitor.Configuration;
using InternetMonitor.Localization;

namespace InternetMonitor.App;

/// <summary>
/// The one question about credentials in settings.json, asked at startup: the file holds a
/// webhook URL, an Uptime Kuma URL or an endpoint header as readable text - protect them with the
/// user's Windows account (<see cref="SecretProtection"/>), or keep them readable? Asked once; the
/// answer is <see cref="AppSettings.ProtectSecrets"/>, changeable in Settings. A user who said yes
/// and later pastes a readable credential into the file gets it protected on the next save
/// without being asked again - they already chose.
///
/// Only the windowed app asks. The command-line export reads the same file and must never block
/// on a dialog; it simply uses the values.
/// </summary>
internal static class SecretProtectionPrompt
{
    public static void ApplyAtStartup(AppSettings settings)
    {
        if (!settings.LoadedPlaintextSecrets)
        {
            return;
        }

        if (settings.ProtectSecrets is null)
        {
            LocalizationManager loc = LocalizationManager.Instance;
            DialogResult answer = MessageBox.Show(
                loc.Get("secrets.prompt.message"),
                loc.Get("secrets.prompt.title"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);
            settings.ProtectSecrets = answer == DialogResult.Yes;
            settings.Save();
        }
        else if (settings.ProtectSecrets == true)
        {
            // A credential typed into the file by hand since the user chose protection.
            settings.Save();
        }
    }
}
