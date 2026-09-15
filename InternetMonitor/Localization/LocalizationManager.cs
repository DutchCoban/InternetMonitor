using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

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
    /// <summary>
    /// Every language the app ships a string table for, in display order (used to populate the
    /// Settings language picker) - the single place that list is defined, so adding a language
    /// means adding one entry here plus its embedded &lt;code&gt;.json, not hunting down every
    /// place a language code is compared or displayed.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages =
    [
        ("nl", "Nederlands"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("pl", "Polski"),
    ];

    private const string DefaultLanguageCode = "nl";

    public static LocalizationManager Instance { get; private set; } = null!;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = DefaultLanguageCode;

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
        CurrentLanguage = SupportedLanguages.Any(l => l.Code == languageCode) ? languageCode : DefaultLanguageCode;
        _strings = LoadLanguageFile(CurrentLanguage);
        AssertKeysMatchInDebug();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>This language's own display name (e.g. "English", "Deutsch"), for showing in UI without a caller needing to look it up in <see cref="SupportedLanguages"/> itself.</summary>
    public string CurrentLanguageDisplayName =>
        SupportedLanguages.FirstOrDefault(l => l.Code == CurrentLanguage).DisplayName ?? CurrentLanguage;

    public string Get(string key) => _strings.TryGetValue(key, out string? value) ? value : $"[{key}]";

    public string Format(string key, params object[] args) => string.Format(Get(key), args);

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
        foreach ((string code, _) in SupportedLanguages)
        {
            if (code == CurrentLanguage)
            {
                continue;
            }

            var otherStrings = LoadLanguageFile(code);
            var missingInOther = _strings.Keys.Except(otherStrings.Keys).ToList();
            var missingInCurrent = otherStrings.Keys.Except(_strings.Keys).ToList();
            Debug.Assert(
                missingInOther.Count == 0 && missingInCurrent.Count == 0,
                $"Localization key mismatch between {CurrentLanguage}.json and {code}.json. " +
                $"Missing in {code}: {string.Join(", ", missingInOther)}. " +
                $"Missing in {CurrentLanguage}: {string.Join(", ", missingInCurrent)}.");
        }
    }
}
