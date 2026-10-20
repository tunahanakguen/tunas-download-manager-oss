using System.Text;
using System.Text.Json;
using TunasDM.Core.Browser;
using TunasDM.Core.Management;
using TunasDM.Host;

namespace TunasDM.Core.Tests;

public class BrowserTests
{
    [Fact]
    public void DownloadMessage_BecomesNewDownload()
    {
        var message = BrowserProtocol.Parse("""
            {"type":"download","v":1,"url":"https://example.com/a/setup.exe","fileName":"setup.exe","fileSize":1234,
             "referrer":"https://example.com/","cookies":"sid=1","userAgent":"Mozilla/5.0"}
            """.ReplaceLineEndings(""))!;

        var download = message.ToNewDownload()!;
        Assert.Equal("https://example.com/a/setup.exe", download.Url.ToString());
        Assert.Equal(("setup.exe", 1234L, "sid=1", "https://example.com/"), (download.FileName, download.KnownSize, download.Cookies, download.Referrer));
    }

    [Theory]
    [InlineData("""{"type":"download","url":"javascript:alert(1)"}""")]
    [InlineData("""{"type":"download","url":"file:///C:/Windows/win.ini"}""")]
    [InlineData("""{"type":"download"}""")]
    public void DownloadMessage_RejectsUnsafeOrMissingUrls(string json)
    {
        Assert.Null(BrowserProtocol.Parse(json)!.ToNewDownload());
    }

    [Fact]
    public void DownloadMessage_StripsPathsFromFileName()
    {
        var message = BrowserProtocol.Parse("""{"type":"download","url":"https://e.com/x","fileName":"..\\..\\evil.exe"}""")!;
        Assert.Equal("evil.exe", message.ToNewDownload()!.FileName);
    }

    [Fact]
    public void Parse_IgnoresBrokenJson() => Assert.Null(BrowserProtocol.Parse("{kein json"));

    [Fact]
    public void Config_ContainsAllCategoryExtensions()
    {
        var settings = new AppSettings { BrowserExcludedSites = ["*.intranet.local"] };
        string json = BrowserProtocol.Serialize(BrowserProtocol.CreateConfig(settings));
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("config", doc.RootElement.GetProperty("type").GetString());
        var extensions = doc.RootElement.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("zip", extensions);
        Assert.Contains("mp4", extensions);
        Assert.Equal("*.intranet.local", doc.RootElement.GetProperty("excludedSites")[0].GetString());
    }

    [Fact]
    public void HostManifest_AllowsOnlyOurExtension()
    {
        using var doc = JsonDocument.Parse(NativeHostRegistration.CreateManifest(@"C:\Programme\TUNAS\TunasDM.Host.exe"));
        Assert.Equal("com.tunas.dm", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(@"C:\Programme\TUNAS\TunasDM.Host.exe", doc.RootElement.GetProperty("path").GetString());
        Assert.Equal(["chrome-extension://moikgldodglaniajmfgdgkmelkolfkap/"],
            doc.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void FirefoxHostManifest_AllowsOnlyOurExtension_AndMatchesExtensionManifest()
    {
        using var doc = JsonDocument.Parse(NativeHostRegistration.CreateFirefoxManifest(@"C:\Programme\TUNAS\TunasDM.Host.exe"));
        Assert.Equal(["tunasdm@tunas"], doc.RootElement.GetProperty("allowed_extensions").EnumerateArray().Select(e => e.GetString()));
        Assert.False(doc.RootElement.TryGetProperty("allowed_origins", out _));

        // Die Gecko-ID im Firefox-Manifest der Erweiterung muss dazu passen.
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extension", "manifest.firefox.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(BrowserProtocol.FirefoxExtensionId,
            manifest.RootElement.GetProperty("browser_specific_settings").GetProperty("gecko").GetProperty("id").GetString());
        Assert.True(manifest.RootElement.GetProperty("background").TryGetProperty("scripts", out _));
        // Gleiche Version wie die Chromium-Fassung.
        using var chromium = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "manifest.json")));
        Assert.Equal(chromium.RootElement.GetProperty("version").GetString(), manifest.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void ExtensionTexts_EveryKeyInGermanAndEnglish()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extension");
        HashSet<string> Keys(string language)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "_locales", language, "messages.json")));
            return doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        }
        var de = Keys("de");
        Assert.Equal(de.Order(), Keys("en").Order());

        // Jeder Schlüssel aus t("…"), getMessage("…") und __MSG_…__ ist vorhanden.
        string code = string.Concat(new[] { "background.js", "content.js", "manifest.json", "manifest.firefox.json" }
            .Select(f => File.ReadAllText(Path.Combine(folder, f))));
        var used = System.Text.RegularExpressions.Regex.Matches(code, @"(?:\bt\(|getMessage\()""(\w+)""|__MSG_(\w+)__")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToHashSet();
        Assert.NotEmpty(used);
        Assert.Empty(used.Except(de));
    }

    [Fact]
    public void ExtensionManifest_KeyMatchesExtensionId()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extension", "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        byte[] key = Convert.FromBase64String(doc.RootElement.GetProperty("key").GetString()!);
        string id = new(System.Security.Cryptography.SHA256.HashData(key).Take(16)
            .SelectMany(b => new[] { b >> 4, b & 15 }).Select(n => (char)('a' + n)).ToArray());

        Assert.Equal(BrowserProtocol.ExtensionId, id);
    }

    [Fact]
    public async Task NativeMessaging_RoundTrips()
    {
        using var stream = new MemoryStream();
        await NativeMessaging.WriteAsync(stream, Encoding.UTF8.GetBytes("""{"type":"hello"}"""));
        await NativeMessaging.WriteAsync(stream, Encoding.UTF8.GetBytes("""{"type":"x","t":"äö"}"""));
        stream.Position = 0;

        Assert.Equal("""{"type":"hello"}""", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(stream))!));
        Assert.Equal("""{"type":"x","t":"äö"}""", Encoding.UTF8.GetString((await NativeMessaging.ReadAsync(stream))!));
        Assert.Null(await NativeMessaging.ReadAsync(stream));
    }

    [Fact]
    public async Task NativeMessaging_RejectsAbsurdLength()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);
        await Assert.ThrowsAsync<InvalidDataException>(() => NativeMessaging.ReadAsync(stream));
    }

    [Fact]
    public void InstallHost_CopiesHostWithAppPath_AndKeepsCopyWhileLocked()
    {
        string root = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));
        string app = Path.Combine(root, "app");
        string target = Path.Combine(root, "host");
        Directory.CreateDirectory(app);
        try
        {
            foreach (string name in NativeHostRegistration.HostFiles)
                File.WriteAllText(Path.Combine(app, name), "v1 " + name);

            string installed = NativeHostRegistration.InstallHost(app, target);

            Assert.Equal(Path.Combine(target, "TunasDM.Host.exe"), installed);
            Assert.All(NativeHostRegistration.HostFiles, f => Assert.Equal("v1 " + f, File.ReadAllText(Path.Combine(target, f))));
            Assert.Equal(Path.Combine(app, "TunasDM.exe"), File.ReadAllText(Path.Combine(target, NativeHostRegistration.AppPathFile)));

            // Host läuft (DLL gesperrt): nichts mischen, alte Kopie bleibt gültig.
            foreach (string name in NativeHostRegistration.HostFiles)
                File.WriteAllText(Path.Combine(app, name), "v2 " + name);
            using (File.Open(Path.Combine(target, "TunasDM.Host.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Equal(installed, NativeHostRegistration.InstallHost(app, target));
            Assert.All(NativeHostRegistration.HostFiles, f => Assert.Equal("v1 " + f, File.ReadAllText(Path.Combine(target, f))));

            // Danach wird aktualisiert.
            NativeHostRegistration.InstallHost(app, target);
            Assert.All(NativeHostRegistration.HostFiles, f => Assert.Equal("v2 " + f, File.ReadAllText(Path.Combine(target, f))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
