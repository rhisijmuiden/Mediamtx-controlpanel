using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace MediaMtxPanel.Services.Localization;

public record PanelLanguage(string Code, string Name, string Flag);

/// <summary>
/// Translates the panel's texts. The English text itself is the key: <c>L["Live now"]</c> returns
/// the translation for the current UI culture, or the English text when there is none.
/// Translations live in <c>Localization/*.json</c> (compiled into the assembly), each file shaped as
/// <c>{ "English text": { "nl": "…", "de": "…", "fr": "…" } }</c>.
/// Placeholders use string.Format syntax: <c>L["{0} viewers", count]</c>.
/// </summary>
public sealed class TextLocalizer
{
    public const string DefaultLanguage = "en";

    public static readonly IReadOnlyList<PanelLanguage> Languages =
    [
        new("en", "English", "🇬🇧"),
        new("nl", "Nederlands", "🇳🇱"),
        new("de", "Deutsch", "🇩🇪"),
        new("fr", "Français", "🇫🇷"),
    ];

    public static readonly string[] Codes = Languages.Select(l => l.Code).ToArray();

    private readonly FrozenDictionary<string, FrozenDictionary<string, string>> byLanguage;

    public TextLocalizer()
    {
        var tables = Codes.ToDictionary(c => c, _ => new Dictionary<string, string>());
        var assembly = typeof(TextLocalizer).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("i18n.") && n.EndsWith(".json")).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            var file = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stream) ?? [];
            foreach (var (english, translations) in file)
                foreach (var (code, text) in translations)
                    if (tables.TryGetValue(code, out var table) && !string.IsNullOrEmpty(text))
                        table[english] = text;
        }
        byLanguage = tables.ToFrozenDictionary(t => t.Key, t => t.Value.ToFrozenDictionary());
    }

    public string this[string english] => Translate(english, CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    public string this[string english, params object?[] args] =>
        string.Format(CultureInfo.CurrentCulture, this[english], args);

    public string Translate(string english, string language) =>
        byLanguage.TryGetValue(language, out var table) && table.TryGetValue(english, out var text) ? text : english;

    /// <summary>Two-letter code of the language the current request or circuit uses.</summary>
    public static string Current => Normalize(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) ?? DefaultLanguage;

    public static string? Normalize(string? code) =>
        Codes.FirstOrDefault(c => string.Equals(c, code?.Trim(), StringComparison.OrdinalIgnoreCase));
}
