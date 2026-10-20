using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TunasDM.Core;

/// <summary>
/// Oberflächensprache. Der deutsche Text ist zugleich der Schlüssel (wie bei gettext): Im Code steht
/// <c>L.T("Speichern in")</c>, das Englische kommt aus <c>Localization/en.json</c>. Fehlt eine Übersetzung,
/// erscheint der deutsche Text – nie ein leerer oder technischer Schlüssel.
/// </summary>
public static class L
{
    private static IReadOnlyDictionary<string, string> _texts = new Dictionary<string, string>();

    /// <summary>„de“ oder „en“.</summary>
    public static string Language { get; private set; } = "de";

    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
        [("", "Wie Windows / System"), ("de", "Deutsch"), ("en", "English")];

    /// <summary>Sprache setzen: „de“, „en“ oder leer = Windows-Sprache (Deutsch nur bei deutschem Windows).</summary>
    public static void Use(string? code)
    {
        string language = string.IsNullOrEmpty(code)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en"
            : code;
        Language = language == "en" ? "en" : "de";
        _texts = Language == "de" ? new Dictionary<string, string>() : Load(Language);
    }

    /// <summary>Übersetzung des deutschen Textes.</summary>
    public static string T(string german) =>
        _texts.TryGetValue(german, out var text) && text.Length > 0 ? text : german;

    /// <summary>Übersetzung mit Platzhaltern <c>{0}</c>, <c>{1}</c> … (Zahlen im Format der Oberflächensprache).</summary>
    public static string F(string germanFormat, params object?[] args) =>
        string.Format(Culture, T(germanFormat), args);

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language == "en" ? "en-US" : "de-DE");

    /// <summary>Alle Übersetzungen einer Sprache (für Tests).</summary>
    public static IReadOnlyDictionary<string, string> Load(string language)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"TunasDM.Core.Localization.{language}.json");
        if (stream is null)
            return new Dictionary<string, string>();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
