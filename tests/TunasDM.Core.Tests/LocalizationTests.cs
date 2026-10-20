using System.Net;
using System.Text.RegularExpressions;

namespace TunasDM.Core.Tests;

/// <summary>Jeder übersetzbare Text im Quellcode hat eine englische Übersetzung mit denselben Platzhaltern.</summary>
public sealed partial class LocalizationTests
{
    private static readonly string SourceRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));

    /// <summary>
    /// Texte aus <c>{l:T '…'}</c> in XAML und aus String-Literalen in Zeilen mit <c>L.T(</c>/<c>L.F(</c> in C#
    /// (ohne Formatangaben wie "g" und ohne $"…"-Literale, die nur zusammensetzen).
    /// </summary>
    public static SortedSet<string> SourceKeys()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(SourceRoot, "*.xaml", SearchOption.AllDirectories).Where(NotBuildOutput))
        {
            foreach (Match m in XamlKey().Matches(File.ReadAllText(file)))
                keys.Add(WebUtility.HtmlDecode(m.Groups[1].Value.Replace(@"\'", "'").Replace(@"\\", @"\")));
        }
        foreach (var file in Directory.GetFiles(SourceRoot, "*.cs", SearchOption.AllDirectories).Where(NotBuildOutput))
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.Contains("L.T(") && !line.Contains("L.F(") || line.TrimStart().StartsWith("//"))
                    continue;
                foreach (Match m in CsLiteral().Matches(line))
                {
                    if (m.Groups["prefix"].Value.Contains('$'))
                        continue;
                    string value = Regex.Unescape(m.Groups["text"].Value);
                    if (!value.Any(char.IsLetter) || FormatOnly().IsMatch(value))
                        continue;
                    keys.Add(value);
                }
            }
        }
        return keys;
    }

    private static bool NotBuildOutput(string path)
    {
        string p = path.Replace('\\', '/');
        return !p.Contains("/obj/") && !p.Contains("/bin/");
    }

    [Fact]
    public void EveryText_HasEnglishTranslation()
    {
        var english = L.Load("en");
        var missing = SourceKeys().Where(k => !english.TryGetValue(k, out var t) || string.IsNullOrWhiteSpace(t)).ToList();
        Assert.True(missing.Count == 0, "Ohne Übersetzung:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Translations_KeepPlaceholders()
    {
        var wrong = L.Load("en")
            .Where(p => !Placeholders(p.Key).SetEquals(Placeholders(p.Value)))
            .Select(p => $"{p.Key} → {p.Value}")
            .ToList();
        Assert.True(wrong.Count == 0, "Platzhalter passen nicht:\n" + string.Join("\n", wrong));
    }

    /// <summary>Ohne die Sprache global umzustellen (andere Tests laufen parallel und prüfen deutsche Meldungen).</summary>
    [Fact]
    public void EnglishDictionary_Loads_AndGermanIsDefault()
    {
        var english = L.Load("en");
        Assert.Equal("Cancel", english["Abbrechen"]);
        Assert.Equal("3 links found, 2 selected", string.Format(english["{0} Links gefunden, {1} ausgewählt"], 3, 2));
        Assert.Empty(L.Load("xx"));
        // Standard ohne L.Use: Deutsch, unbekannte Texte bleiben, wie sie sind.
        Assert.Equal("Abbrechen", L.T("Abbrechen"));
        Assert.Equal("Text, den es nicht gibt", L.T("Text, den es nicht gibt"));
    }

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).ToHashSet();

    [GeneratedRegex(@"\{l:T '((?:[^'\\]|\\.)*)'\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"(?<prefix>\$?@?)""(?<text>(?:[^""\\]|\\.)*)""")]
    private static partial Regex CsLiteral();

    [GeneratedRegex(@"^[dMyHhmsftgT.:\\ \-/]*$")]
    private static partial Regex FormatOnly();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
