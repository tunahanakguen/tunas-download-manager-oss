using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TunasDM.Core.Management;

/// <summary>Ein Link aus einer importierten Datei, mit den Angaben, die die Datei mitbringt.</summary>
public sealed record ImportedLink(Uri Url, string? Text = null, string? Referrer = null, string? UserAgent = null);

/// <summary>
/// Download-Listen lesen und schreiben (Import/Export, kompatibel zu Internet Download Manager):
/// Textdateien (ein Link je Zeile oder irgendwo im Text), IDM-Listen (<c>.ef2</c>) und HTML-Seiten.
/// Cookies werden weder gelesen noch geschrieben: Eine Liste wird weitergegeben, Anmeldungen gehören nicht hinein.
/// </summary>
public static partial class LinkList
{
    public static string FileFilter => L.T(
        "Link-Listen (*.txt;*.ef2;*.htm;*.html)|*.txt;*.ef2;*.htm;*.html|IDM-Liste (*.ef2)|*.ef2|Textdatei (*.txt)|*.txt|HTML-Seite (*.htm;*.html)|*.htm;*.html|Alle Dateien|*.*");

    /// <summary>Erkennt das Format an der Endung, sonst am Inhalt.</summary>
    public static IReadOnlyList<ImportedLink> Parse(string content, string? fileName = null)
    {
        string extension = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        string head = content.TrimStart();
        if (extension == ".ef2" || head.StartsWith('<') && Ef2Start().IsMatch(head))
            return ParseEf2(content);
        if (extension is ".htm" or ".html" || HtmlStart().IsMatch(head))
            return ParseHtml(content);
        return ParseText(content);
    }

    /// <summary>Alle http/https/ftp(s)-Adressen im Text, in der Reihenfolge ihres Auftretens, ohne Doppelte.</summary>
    public static IReadOnlyList<ImportedLink> ParseText(string content) =>
        Distinct(TextUrl().Matches(content).Select(m => TryUrl(m.Value.TrimEnd('.', ',', ';', ':', ')', ']', '\'', '"', '>')))
            .Where(u => u is not null).Select(u => new ImportedLink(u!)));

    /// <summary>
    /// IDM-Format: Blöcke zwischen „&lt;“ und „&gt;“, erste Zeile die Adresse, danach Kopfzeilen
    /// wie „referer: …“ und „User-Agent: …“.
    /// </summary>
    public static IReadOnlyList<ImportedLink> ParseEf2(string content)
    {
        var links = new List<ImportedLink>();
        ImportedLink? current = null;
        foreach (var raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line == "<")
            {
                current = null;
                continue;
            }
            if (line == ">")
            {
                if (current is not null)
                    links.Add(current);
                current = null;
                continue;
            }
            if (line.Length == 0)
                continue;
            if (current is null)
            {
                if (TryUrl(line) is { } url)
                    current = new ImportedLink(url);
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
                continue;
            current = key switch
            {
                "referer" or "referrer" => current with { Referrer = TryUrl(value)?.ToString() },
                "user-agent" => current with { UserAgent = value },
                _ => current, // cookie, postdata, username, password: bewusst nicht übernommen
            };
        }
        if (current is not null)
            links.Add(current); // letzter Block ohne „>“
        return Distinct(links);
    }

    /// <summary>Links aus href/src einer HTML-Seite; relative Adressen über &lt;base href&gt; bzw. die Adresse der Seite.</summary>
    public static IReadOnlyList<ImportedLink> ParseHtml(string content, Uri? pageUrl = null)
    {
        string? baseHref = BaseHref().Match(content) is { Success: true } b ? WebUtility.HtmlDecode(b.Groups["u"].Value).Trim() : null;
        Uri? baseUri = baseHref is null ? pageUrl
            : pageUrl is not null && Uri.TryCreate(pageUrl, baseHref, out var resolved) && IsDownloadScheme(resolved) ? resolved
            : TryUrl(baseHref) ?? pageUrl;
        var links = new List<ImportedLink>();
        foreach (Match m in HtmlLink().Matches(content))
        {
            string value = WebUtility.HtmlDecode(m.Groups["u"].Value).Trim();
            if (value.Length == 0 || value.StartsWith('#'))
                continue; // Sprungmarke auf derselben Seite
            Uri? url = TryUrl(value);
            if (url is null && baseUri is not null && Uri.TryCreate(baseUri, value, out var relative) && IsDownloadScheme(relative))
                url = relative;
            if (url is null)
                continue;
            string? text = m.Groups["t"].Success ? WebUtility.HtmlDecode(Tags().Replace(m.Groups["t"].Value, " ")).Trim() : null;
            links.Add(new ImportedLink(url, string.IsNullOrWhiteSpace(text) ? null : Spaces().Replace(text, " ")));
        }
        return Distinct(links);
    }

    /// <summary>Eine Adresse je Zeile.</summary>
    public static string WriteText(IEnumerable<DownloadItem> items) =>
        string.Join("\r\n", items.Select(i => Secrets.WithoutPassword(i.Url)).Distinct()) + "\r\n";

    /// <summary>IDM-kompatibel; ohne Cookies und ohne Passwörter in der Adresse.</summary>
    public static string WriteEf2(IEnumerable<DownloadItem> items)
    {
        var text = new StringBuilder();
        foreach (var item in items.DistinctBy(i => i.Url))
        {
            text.Append("<\r\n").Append(Secrets.WithoutPassword(item.Url)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(item.Referrer))
                text.Append("referer: ").Append(item.Referrer).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(item.UserAgent))
                text.Append("User-Agent: ").Append(item.UserAgent).Append("\r\n");
            text.Append(">\r\n");
        }
        return text.ToString();
    }

    public static string Write(IEnumerable<DownloadItem> items, string fileName) =>
        Path.GetExtension(fileName).Equals(".ef2", StringComparison.OrdinalIgnoreCase) ? WriteEf2(items) : WriteText(items);

    private static List<ImportedLink> Distinct(IEnumerable<ImportedLink> links) => links.DistinctBy(l => l.Url.ToString()).ToList();

    private static Uri? TryUrl(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var url) && IsDownloadScheme(url) && url.Host.Length > 0 ? url : null;

    private static bool IsDownloadScheme(Uri url) => DownloadSchemes.IsSupported(url);

    [GeneratedRegex(@"\A<\s*\r?\n\s*(https?|ftpe?s?)://", RegexOptions.IgnoreCase)]
    private static partial Regex Ef2Start();

    [GeneratedRegex(@"\A(<!doctype\s+html|<html|<head|<body|<a\s)", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlStart();

    [GeneratedRegex(@"\b(?:https?|ftpe?s?)://[^\s<>""'`]+", RegexOptions.IgnoreCase)]
    private static partial Regex TextUrl();

    [GeneratedRegex(@"<base\s[^>]*href\s*=\s*[""']?(?<u>[^""'\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseHref();

    [GeneratedRegex(@"<a\s[^>]*?href\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))[^>]*>(?<t>.*?)</a>|<(?:img|video|audio|source|embed|iframe|frame)\s[^>]*?src\s*=\s*(?:""(?<u>[^""]*)""|'(?<u>[^']*)'|(?<u>[^\s>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlLink();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
