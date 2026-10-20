using System.Net;
using TunasDM.Core.Management;

using TunasDM.Core;

namespace TunasDM.Core.Grabber;

/// <summary>Einstellungen des Site-Grabbers: Startseite, Tiefe, welche Seiten besucht und welche Dateien gesammelt werden.</summary>
public sealed record GrabberOptions(Uri StartUrl)
{
    /// <summary>0 = nur die Startseite, 1 = auch die von dort verlinkten Seiten usw.</summary>
    public int Depth { get; init; } = 1;

    /// <summary>Gesuchte Endungen ohne Punkt; leer = alle Dateien, die keine Seiten sind.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Nur Seiten auf demselben Host besuchen (Dateien dürfen woanders liegen, z. B. auf einem CDN).</summary>
    public bool SameHostOnly { get; init; } = true;

    /// <summary>Nur Seiten unterhalb des Ordners der Startseite besuchen.</summary>
    public bool BelowStartFolderOnly { get; init; }

    /// <summary>Höchstens so viele Seiten laden.</summary>
    public int MaxPages { get; init; } = 200;

    /// <summary>Anweisungen in robots.txt beachten (Disallow für „*“ bzw. „TUNAS“).</summary>
    public bool RespectRobots { get; init; } = true;

    /// <summary>Gleichzeitig geladene Seiten; klein halten, um den Server nicht zu überlasten.</summary>
    public int Parallel { get; init; } = 3;

    public string? UserAgent { get; init; }
    public string? Cookies { get; init; }
}

/// <summary>Gefundene Datei mit der Seite, auf der sie verlinkt war (Referer).</summary>
public sealed record GrabbedFile(Uri Url, string? Text, Uri Page);

public sealed record GrabberProgress(int PagesVisited, int PagesQueued, int FilesFound, Uri? Current);

/// <summary>
/// Durchsucht eine Website bis zur eingestellten Tiefe und sammelt Links auf Dateien. Seiten werden nur gelesen,
/// wenn sie HTML liefern (höchstens 5 MB); Dateien werden nicht geladen, nur aufgelistet.
/// </summary>
public sealed class SiteGrabber
{
    public const long MaxPageBytes = 5 * 1024 * 1024;

    /// <summary>Endungen, hinter denen meist eine Seite steckt (werden besucht statt gesammelt).</summary>
    private static readonly HashSet<string> PageExtensions =
        ["", "htm", "html", "xhtml", "shtml", "php", "asp", "aspx", "jsp", "cfm", "cgi", "pl"];

    private readonly HttpClient _client;
    private readonly GrabberOptions _options;
    private RobotsRules? _robots;

    // Seiten in windows-1252, iso-8859-x usw.
    static SiteGrabber() => System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    public SiteGrabber(HttpClient client, GrabberOptions options)
    {
        _client = client;
        _options = options;
    }

    public static bool IsPageLike(Uri url) => PageExtensions.Contains(ExtensionOf(url));

    public static string ExtensionOf(Uri url)
    {
        string name = url.AbsolutePath[(url.AbsolutePath.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[(dot + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>Durchsucht bis zur Tiefe oder bis <paramref name="token"/> abbricht; dann gilt, was bis dahin gefunden wurde.</summary>
    public async Task<IReadOnlyList<GrabbedFile>> RunAsync(IProgress<GrabberProgress>? progress, CancellationToken token)
    {
        var start = WithoutFragment(_options.StartUrl);
        if (_options.RespectRobots)
            _robots = await RobotsRules.LoadAsync(_client, start, _options.UserAgent, token).ConfigureAwait(false);

        var seenPages = new HashSet<string> { start.AbsoluteUri };
        var files = new Dictionary<string, GrabbedFile>();
        var level = new List<Uri> { start };
        int visited = 0;
        var wanted = _options.Extensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Where(e => e.Length > 0).ToHashSet();

        for (int depth = 0; level.Count > 0 && depth <= _options.Depth; depth++)
        {
            var next = new List<Uri>();
            using var gate = new SemaphoreSlim(Math.Max(1, _options.Parallel));
            var tasks = new List<Task>();
            foreach (var page in level)
            {
                if (visited >= _options.MaxPages)
                    break;
                visited++;
                try
                {
                    await gate.WaitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        progress?.Report(new GrabberProgress(visited, level.Count + next.Count, files.Count, page));
                        var (final, links) = await FetchLinksAsync(page, start, token).ConfigureAwait(false);
                        lock (files)
                        {
                            // Weiterleitung auf eine schon bekannte Seite: nicht doppelt auswerten.
                            if (final.AbsoluteUri != page.AbsoluteUri && !seenPages.Add(final.AbsoluteUri))
                                return;
                            foreach (var link in links)
                            {
                                var url = WithoutFragment(link.Url);
                                bool pageLike = IsPageLike(url) && url.Scheme is "http" or "https";
                                bool wantedFile = wanted.Count > 0 ? wanted.Contains(ExtensionOf(url)) : !pageLike;
                                if (wantedFile)
                                {
                                    files.TryAdd(url.AbsoluteUri, new GrabbedFile(url, link.Text, page));
                                }
                                else if (pageLike && depth < _options.Depth && MayVisit(url, start) && seenPages.Add(url.AbsoluteUri))
                                {
                                    next.Add(url);
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or DownloadException
                        || ex is OperationCanceledException && !token.IsCancellationRequested)
                    {
                        // Eine Seite, die nicht lädt, hält den Rest nicht auf.
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, token));
            }
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            if (token.IsCancellationRequested)
                break; // Stopp: Gefundenes behalten
            level = next;
            progress?.Report(new GrabberProgress(visited, level.Count, files.Count, null));
        }
        lock (files)
            return files.Values.ToList();
    }

    private bool MayVisit(Uri url, Uri start)
    {
        if (_options.SameHostOnly && !string.Equals(url.IdnHost, start.IdnHost, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_options.BelowStartFolderOnly)
        {
            string folder = start.GetLeftPart(UriPartial.Path);
            folder = folder[..(folder.LastIndexOf('/') + 1)];
            if (!url.AbsoluteUri.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return _robots?.Allows(url) ?? true;
    }

    /// <summary>Lädt eine Seite (nur HTML, begrenzte Größe, Weiterleitungen nur innerhalb http/https) und liest ihre Links.</summary>
    private async Task<(Uri Final, IReadOnlyList<ImportedLink> Links)> FetchLinksAsync(Uri page, Uri start, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await Download.SendFollowingRedirectsAsync(_client, page, (url, sameSite) =>
        {
            // Weiterleitungen nur dorthin, wo der Grabber auch selbst hindürfte (Host, Ordner, robots.txt).
            if (url.AbsoluteUri != page.AbsoluteUri && !MayVisit(url, start))
                throw new DownloadException(L.F("Weiterleitung außerhalb des erlaubten Bereichs: {0}", url));
            return Download.BuildRequest(url, _options.UserAgent, null, _options.Cookies, new Dictionary<string, string>(), sameSite);
        }, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml"))
            return (page, []);
        if (response.Content.Headers.ContentLength > MaxPageBytes)
            return (page, []);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var limited = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            limited.Write(buffer, 0, read);
            if (limited.Length > MaxPageBytes)
                break;
        }
        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = System.Text.Encoding.UTF8;
        try
        {
            if (!string.IsNullOrWhiteSpace(charset))
                encoding = System.Text.Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
        }
        string html = encoding.GetString(limited.GetBuffer(), 0, (int)limited.Length);
        // Nach Weiterleitungen gilt die endgültige Adresse als Basis für relative Links.
        var final = response.RequestMessage?.RequestUri ?? page;
        return (final, LinkList.ParseHtml(html, final));
    }

    private static Uri WithoutFragment(Uri url) =>
        string.IsNullOrEmpty(url.Fragment) ? url : new Uri(url.GetLeftPart(UriPartial.Query));
}

/// <summary>Einfache robots.txt-Auswertung: Disallow/Allow-Präfixe für „*“ bzw. „TUNAS“, längste Regel gewinnt.</summary>
public sealed class RobotsRules
{
    private readonly List<(string Path, bool Allow)> _rules;

    private RobotsRules(List<(string, bool)> rules) => _rules = rules;

    public static RobotsRules Parse(string text)
    {
        var groups = new List<(List<string> Agents, List<(string, bool)> Rules)>();
        (List<string> Agents, List<(string, bool)> Rules)? current = null;
        bool lastWasAgent = false;
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Split('#')[0].Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            string key = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            if (key == "user-agent")
            {
                if (current is null || !lastWasAgent)
                {
                    current = (new List<string>(), new List<(string, bool)>());
                    groups.Add(current.Value);
                }
                current.Value.Agents.Add(value.ToLowerInvariant());
                lastWasAgent = true;
                continue;
            }
            lastWasAgent = false;
            if (current is null)
                continue;
            if (key == "disallow" && value.Length > 0)
                current.Value.Rules.Add((value, false));
            else if (key == "allow" && value.Length > 0)
                current.Value.Rules.Add((value, true));
        }
        var own = groups.FirstOrDefault(g => g.Agents.Any(a => a.Contains("tunas")));
        var any = groups.FirstOrDefault(g => g.Agents.Contains("*"));
        return new RobotsRules(own.Rules ?? any.Rules ?? []);
    }

    public bool Allows(Uri url)
    {
        string path = url.PathAndQuery;
        var match = _rules.Where(r => Matches(path, r.Path)).OrderByDescending(r => r.Path.Length).ThenByDescending(r => r.Allow).FirstOrDefault();
        return match.Path is null || match.Allow;
    }

    /// <summary>Präfix mit „*“ (beliebig) und „$“ (Ende) wie bei den großen Suchmaschinen.</summary>
    private static bool Matches(string path, string rule)
    {
        bool anchored = rule.EndsWith('$');
        string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(anchored ? rule[..^1] : rule).Replace("\\*", ".*") + (anchored ? "$" : "");
        return System.Text.RegularExpressions.Regex.IsMatch(path, pattern);
    }

    public static async Task<RobotsRules?> LoadAsync(HttpClient client, Uri site, string? userAgent, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await Download.SendFollowingRedirectsAsync(client, new Uri(site, "/robots.txt"), (url, _) =>
                Download.BuildRequest(url, userAgent, null, null, new Dictionary<string, string>(), sameSite: false), timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
                return null; // keine robots.txt: alles erlaubt
            string text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Parse(text.Length > 512 * 1024 ? text[..(512 * 1024)] : text);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }
}
