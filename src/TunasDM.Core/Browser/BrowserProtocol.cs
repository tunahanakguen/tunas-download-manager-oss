using System.Text.Json;
using System.Text.Json.Serialization;
using TunasDM.Core.Management;

namespace TunasDM.Core.Browser;

/// <summary>
/// Lesbares JSON-Protokoll zwischen Erweiterung und App (über TunasDM.Host.exe und Named Pipe).
/// Eine Nachricht pro Zeile auf der Pipe; zwischen Browser und Host mit 4-Byte-Längenpräfix (Native Messaging).
/// </summary>
public static class BrowserProtocol
{
    public const int Version = 1;

    /// <summary>Name des Native-Messaging-Hosts (Registry und Manifest).</summary>
    public const string HostName = "com.tunas.dm";

    /// <summary>Feste ID der Erweiterung, abgeleitet aus dem „key“ im manifest.json.</summary>
    public const string ExtensionId = "moikgldodglaniajmfgdgkmelkolfkap";

    /// <summary>Kennung der Firefox-Erweiterung (browser_specific_settings.gecko.id).</summary>
    public const string FirefoxExtensionId = "tunasdm@tunas";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Pipe der App pro Windows-Sitzung.</summary>
    public static string PipeName(int sessionId) => $"TunasDM.Browser.{sessionId}";

    public static BrowserMessage? Parse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<BrowserMessage>(line, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(object message) => JsonSerializer.Serialize(message, Json);

    /// <summary>Was die Erweiterung zum Entscheiden braucht: übernehmen ja/nein und welche Endungen.</summary>
    public static ConfigMessage CreateConfig(AppSettings settings) => new()
    {
        Enabled = settings.BrowserIntegration,
        Extensions = settings.Categories.SelectMany(c => c.Extensions).Distinct().Order().ToList(),
        ExcludedSites = settings.BrowserExcludedSites,
        VideoButton = settings.ShowVideoButton,
    };
}

/// <summary>Nachricht der Erweiterung. „type“ bestimmt, welche Felder gesetzt sind.</summary>
public sealed record BrowserMessage
{
    /// <summary>hello, download oder links.</summary>
    public string Type { get; init; } = "";

    public int V { get; init; }
    public string? Browser { get; init; }
    public string? ExtVersion { get; init; }

    public string? Url { get; init; }
    public string? FileName { get; init; }
    public long? FileSize { get; init; }
    public string? Mime { get; init; }
    public string? Referrer { get; init; }
    public string? Cookies { get; init; }
    public string? UserAgent { get; init; }

    /// <summary>Für „links“: alle gefundenen Links der Seite.</summary>
    public List<LinkInfo>? Links { get; init; }

    /// <summary>Für „media“: Seite im Tab (Url ist der Frame mit dem Video) und Titel.</summary>
    public string? PageUrl { get; init; }

    public string? Title { get; init; }

    /// <summary>Für „media“: direkte Quellen und erkannte HLS-/DASH-Listen, falls die Seite selbst nichts liefert.</summary>
    public List<string>? Streams { get; init; }

    /// <summary>Für „media“: direkte Quelle des angeklickten Videos (leer bei blob:-Quellen wie YouTube).</summary>
    public string? Src { get; init; }

    /// <summary>Für „diag“: Hinweis der Erweiterung fürs Protokoll (z. B. warum kein Video-Symbol erscheint).</summary>
    public string? Text { get; init; }

    /// <summary>
    /// Kandidaten für yt-dlp in sinnvoller Reihenfolge: direkte Quelle des angeklickten Videos, Frame, Seite,
    /// dann erkannte Streams (nur http/https). Die direkte Quelle zuerst, weil eine Seite mehrere Videos haben kann.
    /// </summary>
    public IReadOnlyList<Uri> MediaCandidates() =>
        new[] { Src, Url, PageUrl }.Concat(Streams ?? [])
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri : null)
            .OfType<Uri>()
            .DistinctBy(u => u.ToString())
            .ToList();

    public Uri? TryGetUrl() =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri) && DownloadSchemes.IsSupported(uri) ? uri : null;

    public NewDownload? ToNewDownload() => TryGetUrl() is { } uri
        ? new NewDownload(uri)
        {
            FileName = string.IsNullOrWhiteSpace(FileName) ? null : Path.GetFileName(FileName),
            Referrer = Referrer,
            Cookies = string.IsNullOrEmpty(Cookies) ? null : Cookies,
            UserAgent = UserAgent,
            KnownSize = FileSize is > 0 ? FileSize.Value : -1,
        }
        : null;
}

public sealed record LinkInfo(string Url, string? Text);

public sealed record ConfigMessage
{
    public string Type => "config";
    public int V => BrowserProtocol.Version;
    public bool Enabled { get; init; }
    public List<string> Extensions { get; init; } = new();
    public List<string> ExcludedSites { get; init; } = new();

    /// <summary>Video-Symbol über Videos zeigen.</summary>
    public bool VideoButton { get; init; }
}
