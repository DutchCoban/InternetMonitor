using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternetMonitor.Localization;
using Xunit;

namespace InternetMonitor.Tests.Localization;

/// <summary>
/// Networking terms a professional says in English stay in English in every language, so a user
/// and a help desk mean the same thing. These are the words earlier translations used instead;
/// a new text that brings one back fails here.
/// </summary>
[Collection("Localization")]
public sealed class TerminologyTests
{
    private static readonly Dictionary<string, string[]> Banned = new()
    {
        ["en"] = ["response time", "login required"],
        ["nl"] = ["rondreis", "reactietijd", "pakketverlies", "hertransmissie", "retransmissies", "hartslag", "gemeten verbinding", "opgelost naar", "opgezocht", "pad-mtu", "routering", "route volgen"],
        ["de"] = ["umlaufzeit", "antwortzeit", "latenz", "paketverlust", "neuübertragung", "durchsatz", "pfad-mtu", "nutzlast", "aufgelöst", "verbindungsgeschwindigkeit"],
        ["fr"] = ["temps de réponse", "aller-retour", "perte de paquets", "latence", "passerelle", @"\bsauts?\b", @"\bsondes?\b", "point de terminaison", "débit descendant", "débit montant"],
        ["es"] = ["tiempo de respuesta", "ida y vuelta", "pérdida de paquetes", "latencia", "puerta de enlace", @"\bsaltos?\b", @"\bsondas?\b", "punto de conexión", "punto final"],
        ["it"] = ["tempo di risposta", "andata e ritorno", "perdita di pacchetti", "latenza", @"\bsalt[oi]\b", @"\bsond[ae]\b", "punto finale"],
        ["pt"] = ["tempo de resposta", "ida e volta", "perda de pacotes", "latência", @"\bsaltos?\b", @"\bsondas?\b", "ponto final", "transferência"],
        ["pl"] = [@"\bopóźnieni[ea]\b", "czas odpowiedzi", "utrata pakiet", "utraty", @"\bbram[aęy]\b", @"\b(prze)?skok", "pobieranie", "sygnał do", "mtu ścieżki", "ładunek", "rozwiązano na", "rozwiązane przez", "rozwiązywanie nazw", "pakiet testowy", "prędkość łącza", "punkt[óuy]?w? końcow"],
    };

    [Theory]
    [InlineData("en")]
    [InlineData("nl")]
    [InlineData("de")]
    [InlineData("pl")]
    [InlineData("fr")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("pt")]
    public void No_language_translates_a_networking_term(string language)
    {
        Dictionary<string, string> texts = Load(language);
        List<string> offenders = [];
        foreach ((string key, string text) in texts)
        {
            foreach (string word in Banned[language])
            {
                if (Regex.IsMatch(text, word, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    offenders.Add($"{key}: '{word}' in \"{text}\"");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Labels_recorded_under_the_old_wording_still_read_back_in_english()
    {
        Assert.Equal("Metered connection", LocalizationManager.ToEnglish("Gemeten verbinding"));
        Assert.Equal("Resolved to", LocalizationManager.ToEnglish("Opgelost naar"));
        Assert.Equal("Captive portal", LocalizationManager.ToEnglish("Login page"));
        Assert.Equal("Latency", LocalizationManager.ToEnglish("Latenz"));
        Assert.Equal("Gateway", LocalizationManager.ToEnglish("Brama"));
        Assert.Contains("Brama", LocalizationManager.TranslationsOf("measure.gateway"));
    }

    private static Dictionary<string, string> Load(string language)
    {
        using Stream stream = typeof(LocalizationManager).Assembly.GetManifestResourceStream($"InternetMonitor.Localization.{language}.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
