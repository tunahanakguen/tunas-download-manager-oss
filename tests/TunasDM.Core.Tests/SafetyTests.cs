using Microsoft.Data.Sqlite;
using TunasDM.Core.Management;

namespace TunasDM.Core.Tests;

/// <summary>Cookies, Weiterleitungen, Speicherplatz, Proxy, Duplikate und abgelaufene Links.</summary>
public sealed class SafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public SafetyTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DownloadOptions Options() => new()
    {
        MaxConnections = 2,
        MinSegmentSize = 128 * 1024,
        BufferSize = 16 * 1024,
        ReadTimeout = TimeSpan.FromSeconds(10),
        RetryDelay = TimeSpan.FromMilliseconds(50),
    };

    [Fact]
    public async Task Redirect_ToOtherHost_SendsNoCookies()
    {
        await using var server = await TestFileServer.StartAsync(512 * 1024);
        // 127.0.0.1 → localhost: anderer Host, gleicher Server.
        server.RedirectTarget = new UriBuilder(server.FileUri) { Host = "localhost" }.Uri;
        var request = new DownloadRequest(server.RedirectUri) { TargetDirectory = _directory, FileName = "a.bin", Cookies = "sid=geheim" };

        var download = Download.Create(request, Options());
        await download.RunAsync();

        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
        var requests = server.CookieHeaders.ToList();
        Assert.All(requests.Where(r => r.Path == "/redirect"), r => Assert.Equal("sid=geheim", r.Cookie));
        Assert.Contains(requests, r => r.Path == "/files/test.bin");
        Assert.All(requests.Where(r => r.Path == "/files/test.bin"), r => Assert.Equal("", r.Cookie));
    }

    [Fact]
    public async Task Redirect_SameHost_KeepsCookies()
    {
        await using var server = await TestFileServer.StartAsync(256 * 1024);
        server.RedirectTarget = server.FileUri;
        var request = new DownloadRequest(server.RedirectUri) { TargetDirectory = _directory, FileName = "b.bin", Cookies = "sid=1" };

        await Download.Create(request, Options()).RunAsync();

        Assert.All(server.CookieHeaders, r => Assert.Equal("sid=1", r.Cookie));
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/b", true)]
    [InlineData("https://example.com/a", "https://cdn.example.com/b", true)]
    [InlineData("https://example.com/a", "https://evil-example.com/b", false)]
    [InlineData("https://example.com/a", "https://cdn.other.net/b", false)]
    [InlineData("https://example.com/a", "http://example.com/b", false)]
    [InlineData("http://example.com/a", "https://example.com/b", true)]
    public void IsSameSite_OnlyOriginHostAndSubdomains(string origin, string target, bool expected)
    {
        Assert.Equal(expected, Download.IsSameSite(new Uri(origin), new Uri(target)));
    }

    [Fact]
    public void StorageCheck_RejectsTooLargeForFat32AndTooLittleSpace()
    {
        long fiveGb = 5L * 1024 * 1024 * 1024;
        var fat = Assert.Throws<DownloadException>(() => StorageCheck.Check(@"E:\", "FAT32", fiveGb * 10, fiveGb));
        Assert.Contains("FAT32", fat.Message);
        var full = Assert.Throws<DownloadException>(() => StorageCheck.Check(@"C:\", "NTFS", 100 * 1024 * 1024, 200 * 1024 * 1024));
        Assert.Contains("Speicherplatz", full.Message);

        StorageCheck.Check(@"C:\", "NTFS", fiveGb * 2, fiveGb);
        StorageCheck.Check(@"E:\", "FAT32", fiveGb, StorageCheck.Fat32MaxFileSize);
    }

    [Fact]
    public void Secrets_RoundTrip_AndReadOldPlainText()
    {
        string protectedValue = Secrets.Protect("sid=abc; token=xyz")!;
        Assert.StartsWith("dpapi:", protectedValue);
        Assert.DoesNotContain("abc", protectedValue);
        Assert.Equal("sid=abc; token=xyz", Secrets.Unprotect(protectedValue));
        Assert.Equal("alt=klartext", Secrets.Unprotect("alt=klartext"));
        Assert.Null(Secrets.Unprotect("dpapi:kaputt!!"));
        Assert.Null(Secrets.Protect(null));
    }

    [Fact]
    public void Database_StoresCookiesEncrypted()
    {
        string path = Path.Combine(_directory, "c.db");
        var database = new DownloadDatabase(path);
        database.Insert(new DownloadItem { Url = "https://example.com/a.zip", Directory = _directory, Cookies = "sid=geheim" });

        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT cookies FROM downloads";
            string raw = (string)command.ExecuteScalar()!;
            Assert.StartsWith("dpapi:", raw);
            Assert.DoesNotContain("geheim", raw);
        }
        Assert.Equal("sid=geheim", Assert.Single(database.LoadAll()).Cookies);
    }

    [Fact]
    public void StateFile_StoresCookiesEncrypted()
    {
        string path = Path.Combine(_directory, "x.bin" + DownloadStateStore.StateExtension);
        var state = new DownloadState { Url = "https://example.com/x.bin", FilePath = "x.bin", Cookies = "sid=geheim" };

        DownloadStateStore.Save(path, state);

        Assert.DoesNotContain("geheim", File.ReadAllText(path));
        Assert.Equal("sid=geheim", state.Cookies);
        Assert.Equal("sid=geheim", DownloadStateStore.Load(path).Cookies);
    }

    [Fact]
    public void Proxy_ParsesAddressesAndBuildsYtDlpArgument()
    {
        Assert.Equal("http://proxy.firma.de:8080/", new ProxySettings(ProxyMode.Manual, "proxy.firma.de:8080").ProxyUri?.ToString());
        Assert.Equal("socks5://127.0.0.1:1080/", new ProxySettings(ProxyMode.Manual, " socks5://127.0.0.1:1080 ").ProxyUri?.ToString());
        Assert.Null(new ProxySettings(ProxyMode.Manual, "socks5://127.0.0.1").ProxyUri);
        Assert.Null(new ProxySettings(ProxyMode.Manual, "").ProxyUri);
        Assert.Null(new ProxySettings(ProxyMode.System, "proxy:8080").ProxyUri);

        Assert.Equal("", new ProxySettings(ProxyMode.None).YtDlpArgument());
        Assert.Null(new ProxySettings(ProxyMode.System).YtDlpArgument());
        Assert.Equal("http://anna:p%40ss@proxy:8080", new ProxySettings(ProxyMode.Manual, "proxy:8080", "anna", "p@ss").YtDlpArgument());
    }

    [Fact]
    public void Settings_StoreProxyPasswordEncrypted()
    {
        var settings = new AppSettings { ProxyMode = ProxyMode.Manual, ProxyAddress = "proxy:8080", ProxyUser = "anna" };
        settings.SetProxyPassword("geheim");

        Assert.DoesNotContain("geheim", System.Text.Json.JsonSerializer.Serialize(settings));
        Assert.Equal("geheim", settings.Proxy().Password);
    }

    [Fact]
    public async Task Proxy_IsUsedForDownloads()
    {
        // Der Testserver spielt den Proxy: ein HTTP-Proxy bekommt die volle Ziel-URL in der Anfragezeile.
        await using var proxy = await TestFileServer.StartAsync(64 * 1024);
        using var client = Download.CreateHttpClient(new ProxySettings(ProxyMode.Manual, proxy.FileUri.Authority));
        var request = new DownloadRequest(new Uri("http://download.invalid/files/test.bin")) { TargetDirectory = _directory };

        var download = Download.Create(request, Options(), client);
        await download.RunAsync();

        Assert.Equal(proxy.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    private DownloadManager NewManager(out AppSettings settings)
    {
        settings = new AppSettings { DownloadsFolder = _directory, MaxConnections = 2 };
        return new DownloadManager(new DownloadDatabase(Path.Combine(_directory, "m.db")), settings, Download.CreateHttpClient());
    }

    [Fact]
    public async Task Manager_FindsDuplicates_PreferringUnfinished()
    {
        await using var manager = NewManager(out _);
        var url = new Uri("https://example.com/datei.zip");
        Assert.Null(manager.FindDuplicate(url));

        var first = manager.Add(new NewDownload(url), startNow: false);
        Assert.Same(first, manager.FindDuplicate(url));
        Assert.Null(manager.FindDuplicate(new Uri("https://example.com/andere.zip")));
    }

    [Fact]
    public async Task ExpiredLink_FailsWithHint_AndResumesAfterUpdatingAddress()
    {
        await using var server = await TestFileServer.StartAsync(512 * 1024);
        await using var manager = NewManager(out _);
        var failed = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<DownloadItem>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.ItemFailed += (_, item) => failed.TrySetResult(item);
        manager.ItemCompleted += (_, item) => completed.TrySetResult(item);

        server.StatusOverride = 403;
        var item = manager.Add(new NewDownload(new Uri(server.FileUri, "/files/test.bin?token=alt")) { FileName = "film.bin" }, startNow: true);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Contains("Adresse aktualisieren", item.Error);

        server.StatusOverride = null;
        manager.UpdateAddress(item, new Uri(server.FileUri, "/files/test.bin?token=neu"), "sid=neu", null, null);
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Null(item.Error);
        manager.Start(item);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.EndsWith("token=neu", item.Url);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(item.FilePath!));
        Assert.Contains(server.CookieHeaders, r => r.Path.EndsWith("token=neu") && r.Cookie == "sid=neu");
    }

    [Fact]
    public async Task UpdateAddress_RewritesStateFileForResume()
    {
        await using var manager = NewManager(out _);
        var item = manager.Add(new NewDownload(new Uri("https://example.com/alt.bin")), startNow: false);
        manager.Pause(item);
        item.FilePath = Path.Combine(_directory, "alt.bin");
        string statePath = DownloadStateStore.StatePathOf(item.FilePath);
        DownloadStateStore.Save(statePath, new DownloadState { Url = item.Url, FilePath = item.FilePath, TotalSize = 10, SupportsRanges = true });

        manager.UpdateAddress(item, new Uri("https://mirror.example.com/alt.bin?sig=2"), "a=1", "https://example.com/", null);

        var state = DownloadStateStore.Load(statePath);
        Assert.Equal(("https://mirror.example.com/alt.bin?sig=2", "a=1", "https://example.com/"), (state.Url, state.Cookies, state.Referrer));
        Assert.Equal(10, state.TotalSize);
    }
}
