using TunasDM.Core.Management;

namespace TunasDM.Core.Tests;

public sealed class LinkListTests
{
    [Fact]
    public void ParseText_FindsUrlsAnywhere_WithoutDuplicatesOrPunctuation()
    {
        const string text = """
            https://example.com/a.zip
            Siehe auch (https://example.com/b.pdf), oder ftp://files.example.org/c.iso.
            https://example.com/a.zip
            mailto:x@example.com  javascript:alert(1)  file:///C:/x
            """;
        var links = LinkList.Parse(text, "liste.txt");
        Assert.Equal(["https://example.com/a.zip", "https://example.com/b.pdf", "ftp://files.example.org/c.iso"],
            links.Select(l => l.Url.ToString()));
    }

    [Fact]
    public void ParseEf2_ReadsReferrerAndUserAgent_IgnoresCookies()
    {
        const string ef2 = "<\r\nhttps://cdn.example.com/x.zip\r\nreferer: https://example.com/page\r\nUser-Agent: Mozilla/5.0 Test\r\n" +
                           "cookie: session=geheim\r\n>\r\n<\r\nhttps://cdn.example.com/y.zip\r\n>\r\n<\r\nkein-link\r\n>\r\n";
        var links = LinkList.Parse(ef2, "export.ef2");

        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/page", links[0].Referrer);
        Assert.Equal("Mozilla/5.0 Test", links[0].UserAgent);
        Assert.Null(links[1].Referrer);
    }

    [Fact]
    public void Parse_DetectsEf2AndHtmlByContent()
    {
        Assert.Equal("https://example.com/r", LinkList.Parse("<\nhttps://example.com/a\nreferer: https://example.com/r\n>")[0].Referrer);
        Assert.Equal("Datei", LinkList.Parse("<html><a href=\"https://example.com/a.zip\">Datei</a></html>")[0].Text);
    }

    [Fact]
    public void ParseHtml_ResolvesWithBase_DecodesEntities_TakesMediaSources()
    {
        const string html = """
            <!doctype html><html><head><base href="https://example.com/dl/"></head><body>
            <a href="setup.exe">Setup <b>1.0</b></a>
            <a href='https://other.org/x?a=1&amp;b=2'>X</a>
            <a href="#oben">Anker</a> <a href="javascript:void(0)">JS</a>
            <img src="bild.png"><video src="https://cdn.example.com/v.mp4"></video>
            </body></html>
            """;
        var links = LinkList.Parse(html, "seite.html");
        Assert.Equal(
            ["https://example.com/dl/setup.exe", "https://other.org/x?a=1&b=2", "https://example.com/dl/bild.png", "https://cdn.example.com/v.mp4"],
            links.Select(l => l.Url.ToString()));
        Assert.Equal("Setup 1.0", links[0].Text);
    }

    [Fact]
    public void ParseHtml_WithoutBase_SkipsRelativeLinks()
    {
        var links = LinkList.ParseHtml("<a href=\"datei.zip\">rel</a><a href=\"https://example.com/abs.zip\">abs</a>");
        Assert.Equal(["https://example.com/abs.zip"], links.Select(l => l.Url.ToString()));
    }

    [Fact]
    public void WriteEf2_RoundTrips_WithoutCookies()
    {
        var items = new[]
        {
            new DownloadItem { Url = "https://cdn.example.com/x.zip", Referrer = "https://example.com/", UserAgent = "UA 1", Cookies = "session=geheim" },
            new DownloadItem { Url = "https://cdn.example.com/y.zip" },
            new DownloadItem { Url = "https://cdn.example.com/x.zip" },
        };
        string ef2 = LinkList.Write(items, "liste.EF2");

        Assert.DoesNotContain("geheim", ef2);
        var back = LinkList.Parse(ef2, "liste.ef2");
        Assert.Equal(2, back.Count);
        Assert.Equal(new ImportedLink(new Uri("https://cdn.example.com/x.zip"), null, "https://example.com/", "UA 1"), back[0]);

        string text = LinkList.Write(items, "liste.txt");
        Assert.Equal("https://cdn.example.com/x.zip\r\nhttps://cdn.example.com/y.zip\r\n", text);
    }
}
