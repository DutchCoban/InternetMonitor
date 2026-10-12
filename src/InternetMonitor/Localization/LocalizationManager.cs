using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace InternetMonitor.Localization;

/// <summary>
/// Loads one language's string table from an embedded JSON resource and exposes lookups plus a
/// change notification. <see cref="Instance"/> is a startup-ordering contract, not a lazy
/// singleton: <see cref="Initialize"/> must be called exactly once, very early in startup, before
/// anything reads <see cref="Instance"/>. There is deliberately no null-check or lazy fallback -
/// a missing call is a startup-sequencing bug and should fail loudly rather than quietly produce
/// an app with no text in it.
///
/// Unlike v1, a key missing from the active language falls back to the English table rather than
/// rendering as "[some.key]" on screen. v2 adds a large amount of new text (step names, hop
/// annotations, three plain-language fields per root cause) and a translation that lags behind by
/// one key should degrade to English, not to visible debug output. The DEBUG-only assertion still
/// catches the mismatch during development.
/// </summary>
public sealed class LocalizationManager
{
    /// <summary>
    /// Every language the app ships a string table for, in display order. The single place that
    /// list is defined, so adding a language means one entry here plus its embedded .json - not
    /// hunting down every place a language code is compared or displayed.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedLanguages =
    [
        ("nl", "Nederlands"),
        ("en", "English"),
        ("de", "Deutsch"),
        ("pl", "Polski"),
    ];

    private const string FallbackLanguageCode = "en";
    private const string DefaultLanguageCode = "nl";

    private static readonly Lazy<Dictionary<string, string>> FallbackStrings =
        new(() => LoadLanguageFile(FallbackLanguageCode));

    private Dictionary<string, string> _strings = new();

    public static LocalizationManager Instance { get; private set; } = null!;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage { get; private set; } = DefaultLanguageCode;

    public static void Initialize(string language) => Instance = new LocalizationManager(language);

    private LocalizationManager(string language) => SetLanguage(language);

    /// <summary>This language's own display name, for showing in the UI without the caller having to look it up.</summary>
    public string CurrentLanguageDisplayName =>
        SupportedLanguages.FirstOrDefault(l => l.Code == CurrentLanguage).DisplayName ?? CurrentLanguage;

    public void SetLanguage(string languageCode)
    {
        CurrentLanguage = SupportedLanguages.Any(l => l.Code == languageCode) ? languageCode : DefaultLanguageCode;
        _strings = LoadLanguageFile(CurrentLanguage);
        AssertKeysMatchInDebug();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out string? value))
        {
            return value;
        }

        return FallbackStrings.Value.TryGetValue(key, out string? fallback) ? fallback : $"[{key}]";
    }

    public string Format(string key, params object[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // A translation with a malformed placeholder must not take the window down with it;
            // showing the raw template is ugly but recoverable, and the DEBUG assertion plus the
            // key-parity test are where this gets caught properly.
            return template;
        }
    }

    private static Dictionary<string, string> LoadLanguageFile(string languageCode)
    {
        string resourceName = $"InternetMonitor.Localization.{languageCode}.json";
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded localization resource not found: {resourceName}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
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

            Dictionary<string, string> other = LoadLanguageFile(code);
            List<string> missingInOther = _strings.Keys.Except(other.Keys).ToList();
            List<string> missingInCurrent = other.Keys.Except(_strings.Keys).ToList();
            Debug.Assert(
                missingInOther.Count == 0 && missingInCurrent.Count == 0,
                $"Localization key mismatch between {CurrentLanguage}.json and {code}.json. "
                + $"Missing in {code}: {string.Join(", ", missingInOther)}. "
                + $"Missing in {CurrentLanguage}: {string.Join(", ", missingInCurrent)}.");
        }
    }
}
