using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TunasDM.Core.Grabber;

namespace TunasDM.Core.Tests;

/// <summary>Kleine Website: Startseite → Unterseiten → Dateien, dazu robots.txt, Weiterleitung, fremder Host.</summary>
internal sealed class TestSite : IAsyncDisposable
{
    private readonly WebApplication _app;
    public ConcurrentQueue<string> Requests { get; } = new();
    public Dictionary<string, (string Type, string Body)> Pages { get; } = new();
    public Uri Root { get; private set; } = null!;

    private TestSite(WebApplication app) => _app = app;

    public static async Task<TestSite> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var site = new TestSite(app);
        app.Run(async context =>
        {
            string path = context.Request.Path + context.Request.QueryString;
            site.Requests.Enqueue(path);
            if (path == "/alt.html")
            {
                context.Response.Redirect("/docs/index.html");
                return;
            }
            if (!site.Pages.TryGetValue(path, out var page))
            {
                context.Response.StatusCode = 404;
                return;
            }
            context.Response.ContentType = page.Type;
            await context.Response.WriteAsync(page.Body);
        });
        await app.StartAsync();
        site.Root = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/");
        return site;
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();
}

public sealed class GrabberTests
{
    private static async Task<TestSite> SiteAsync(string? robots = null)
    {
        var site = await TestSite.StartAsync();
        const string html = "text/html; charset=utf-8";
        site.Pages["/index.html"] = (html, """
            <html><body>
            <a href="docs/index.html">Doku</a> <a href="/alt.html">Alt</a> <a href="#top">Oben</a>
            <a href="bilder/galerie.php?seite=1">Galerie</a>
            <a href="setup.zip">Setup</a> <img src="logo.png">
            <a href="https://cdn.example.com/extern.pdf">Extern</a> <a href="https://other.example.com/seite.html">Fremde Seite</a>
            <a href="mailto:a@b.c">Mail</a>
            </body></html>
            """);
        site.Pages["/docs/index.html"] = (html, """
            <a href="handbuch.pdf">Handbuch</a> <a href="kapitel/eins.html">Kapitel 1</a> <a href="../index.html">Zurück</a>
            """);
        site.Pages["/docs/kapitel/eins.html"] = (html, """<a href="anhang.pdf">Anhang</a>""");
        site.Pages["/bilder/galerie.php?seite=1"] = (html, """<img src="a.jpg"><img src="b.jpg"><a href="galerie.php?seite=2">weiter</a>""");
        site.Pages["/bilder/galerie.php?seite=2"] = (html, """<img src="c.jpg">""");
        site.Pages["/geheim/index.html"] = (html, """<a href="geheim.pdf">x</a>""");
        site.Pages["/setup.zip"] = ("application/zip", "PK");
        if (robots is not null)
            site.Pages["/robots.txt"] = ("text/plain", robots);
        return site;
    }

    private static async Task<List<string>> GrabAsync(TestSite site, GrabberOptions options)
    {
        using var client = Download.CreateHttpClient();
        var files = await new SiteGrabber(client, options).RunAsync(null, default);
        return files.Select(f => f.Url.AbsolutePath.TrimStart('/') + (f.Url.Host == "127.0.0.1" ? "" : "@" + f.Url.Host)).Order().ToList();
    }

    [Fact]
    public async Task DepthZero_OnlyStartPage()
    {
        await using var site = await SiteAsync();
        var files = await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 0 });
        Assert.Equal(["extern.pdf@cdn.example.com", "logo.png", "setup.zip"], files);
        Assert.DoesNotContain(site.Requests, r => r.StartsWith("/docs"));
    }

    [Fact]
    public async Task DepthTwo_FollowsSubpages_RedirectsAndQueries_NotForeignHosts()
    {
        await using var site = await SiteAsync();
        var files = await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 2 });

        Assert.Equal(["bilder/a.jpg", "bilder/b.jpg", "bilder/c.jpg", "docs/handbuch.pdf", "docs/kapitel/anhang.pdf",
            "extern.pdf@cdn.example.com", "logo.png", "setup.zip"], files);
        // Jede Seite einmal (die Weiterleitung /alt.html zeigt auf dieselbe Seite und darf sie höchstens noch einmal holen),
        // Dateien gar nicht.
        Assert.Single(site.Requests, r => r == "/alt.html");
        Assert.InRange(site.Requests.Count(r => r == "/docs/index.html"), 1, 2);
        Assert.Single(site.Requests, r => r == "/docs/kapitel/eins.html");
        Assert.DoesNotContain(site.Requests, r => r is "/setup.zip" or "/logo.png");
    }

    [Fact]
    public async Task ExtensionFilter_AndBelowStartFolder()
    {
        await using var site = await SiteAsync();
        var pdfs = await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 3, Extensions = ["PDF", ".pdf"] });
        Assert.Equal(["docs/handbuch.pdf", "docs/kapitel/anhang.pdf", "extern.pdf@cdn.example.com"], pdfs);

        var below = await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "docs/index.html")) { Depth = 3, BelowStartFolderOnly = true });
        Assert.Equal(["docs/handbuch.pdf", "docs/kapitel/anhang.pdf"], below);
    }

    [Fact]
    public async Task MaxPages_StopsEarly()
    {
        await using var site = await SiteAsync();
        await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 5, MaxPages = 2 });
        Assert.Equal(2, site.Requests.Count(r => r != "/robots.txt"));
    }

    [Fact]
    public async Task Robots_DisallowIsRespected_UnlessSwitchedOff()
    {
        await using var site = await SiteAsync("User-agent: *\nDisallow: /docs/\nAllow: /docs/kapitel/\n");
        var polite = await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 3 });
        Assert.DoesNotContain("docs/handbuch.pdf", polite);
        Assert.DoesNotContain(site.Requests, r => r == "/docs/index.html");

        var all = await GrabAsync(site, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 3, RespectRobots = false });
        Assert.Contains("docs/handbuch.pdf", all);
    }

    [Fact]
    public void RobotsRules_OwnGroupWins_LongestRuleWins_Wildcards()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /

            User-agent: TUNAS
            Disallow: /privat
            Allow: /privat/oeffentlich
            Disallow: /*.cgi$
            """);
        Assert.True(rules.Allows(new Uri("https://x.de/index.html")));
        Assert.False(rules.Allows(new Uri("https://x.de/privat/a.html")));
        Assert.True(rules.Allows(new Uri("https://x.de/privat/oeffentlich/a.html")));
        Assert.False(rules.Allows(new Uri("https://x.de/bin/run.cgi")));
        Assert.True(rules.Allows(new Uri("https://x.de/bin/run.cgi?x=1")));
    }

    [Theory]
    [InlineData("https://x.de/", true)]
    [InlineData("https://x.de/a/b.html", true)]
    [InlineData("https://x.de/liste.php?seite=2", true)]
    [InlineData("https://x.de/ordner", true)]
    [InlineData("https://x.de/datei.zip", false)]
    [InlineData("https://x.de/bild.JPG", false)]
    public void IsPageLike_ByExtension(string url, bool page) => Assert.Equal(page, SiteGrabber.IsPageLike(new Uri(url)));
}

public sealed class GrabberStopTests
{
    [Fact]
    public async Task Stop_KeepsWhatWasFound()
    {
        await using var site = await TestSite.StartAsync();
        site.Pages["/index.html"] = ("text/html", "<a href=\"a.zip\">a</a><a href=\"p1.html\">p</a>");
        site.Pages["/p1.html"] = ("text/html", "<a href=\"b.zip\">b</a>");
        using var client = Download.CreateHttpClient();
        using var cts = new CancellationTokenSource();
        // Synchroner Fortschritt, damit der Stopp sicher nach der ersten Seite greift.
        var grabber = new SiteGrabber(client, new GrabberOptions(new Uri(site.Root, "index.html")) { Depth = 3, RespectRobots = false, Parallel = 1 });
        var files = await grabber.RunAsync(new SyncProgress(p => { if (p.FilesFound > 0) cts.Cancel(); }), cts.Token);
        Assert.Contains(files, f => f.Url.AbsolutePath == "/a.zip");
        Assert.DoesNotContain(files, f => f.Url.AbsolutePath == "/b.zip");
    }

    private sealed class SyncProgress(Action<GrabberProgress> report) : IProgress<GrabberProgress>
    {
        public void Report(GrabberProgress value) => report(value);
    }
}
