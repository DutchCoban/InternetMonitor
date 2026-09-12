using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using InternetMonitor.Network;

namespace InternetMonitor.Localization;

/// <summary>
/// Loads one language's string table from an embedded JSON resource and exposes lookups plus a
/// change notification. <see cref="Instance"/> is a startup-ordering contract, not a lazy
/// singleton: <see cref="Initialize"/> must be called exactly once, very early in Program
/// startup, before anything calls <see cref="Instance"/> - there is no null-check or fallback,
/// by design, since a missing call here indicates a startup-sequencing bug that should fail loudly
/// rather than silently.
/// </summary>
public sealed class LocalizationManager
{
    public static LocalizationManager Instance { get; private set; } = null!;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = "nl";

    private Dictionary<string, string> _strings = new();

    public static void Initialize(string language)
    {
        Instance = new LocalizationManager(language);
    }

    private LocalizationManager(string language)
    {
        SetLanguage(language);
    }

    public void SetLanguage(string languageCode)
    {
        CurrentLanguage = languageCode == "en" ? "en" : "nl";
        _strings = LoadLanguageFile(CurrentLanguage);
        AssertKeysMatchInDebug();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key) => _strings.TryGetValue(key, out string? value) ? value : $"[{key}]";

    public string Format(string key, params object[] args) => string.Format(Get(key), args);

    public string TrayTooltip(ConnectivityState state) => state switch
    {
        ConnectivityState.Connected => Get("tray.tooltip.connected"),
        ConnectivityState.Outage => Get("tray.tooltip.outage"),
        _ => Get("tray.tooltip.checking"),
    };

    private static Dictionary<string, string> LoadLanguageFile(string languageCode)
    {
        string resourceName = $"InternetMonitor.Localization.{languageCode}.json";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded localization resource not found: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? new Dictionary<string, string>();
    }

    [Conditional("DEBUG")]
    private void AssertKeysMatchInDebug()
    {
        string other = CurrentLanguage == "nl" ? "en" : "nl";
        var otherStrings = LoadLanguageFile(other);
        var missingInOther = _strings.Keys.Except(otherStrings.Keys).ToList();
        var missingInCurrent = otherStrings.Keys.Except(_strings.Keys).ToList();
        Debug.Assert(
            missingInOther.Count == 0 && missingInCurrent.Count == 0,
            $"Localization key mismatch between nl.json and en.json. " +
            $"Missing in {other}: {string.Join(", ", missingInOther)}. " +
            $"Missing in {CurrentLanguage}: {string.Join(", ", missingInCurrent)}.");
    }
}
