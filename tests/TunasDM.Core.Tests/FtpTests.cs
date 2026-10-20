using System.Diagnostics;
using TunasDM.Core.Ftp;

namespace TunasDM.Core.Tests;

public sealed class FtpTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public FtpTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static DownloadOptions FastOptions(int connections = 4) => new()
    {
        MaxConnections = connections,
        MinSegmentSize = 256 * 1024,
        BufferSize = 16 * 1024,
        SaveInterval = TimeSpan.FromMilliseconds(100),
        ReadTimeout = TimeSpan.FromSeconds(10),
        RetryDelay = TimeSpan.FromMilliseconds(50),
        DropSlowConnections = false,
    };

    private DownloadRequest Request(Uri url) => new(url) { TargetDirectory = _directory };

    [Fact]
    public async Task Ftp_SeveralConnectionsWithRest_ContentMatches()
    {
        await using var server = TestFtpServer.Start(6 * 1024 * 1024);
        server.ChunkDelayMs = 2;

        var download = Download.Create(Request(server.FileUri), FastOptions());
        await download.RunAsync();

        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
        Assert.Equal("test.bin", Path.GetFileName(download.FilePath));
        Assert.True(download.SupportsRanges);
        Assert.True(server.MaxConcurrent > 1, "nur eine Verbindung");
        Assert.Contains(server.RestOffsets, offset => offset > 0);
        // Änderungsdatum vom Server übernommen.
        Assert.Equal(server.Modified, File.GetLastWriteTimeUtc(download.FilePath!));
        Assert.Equal([Path.GetFileName(download.FilePath!)], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Ftp_WithoutRest_LoadsWithOneConnection()
    {
        await using var server = TestFtpServer.Start(1024 * 1024);
        server.SupportsRest = false;

        var download = Download.Create(Request(server.FileUri), FastOptions());
        await download.RunAsync();

        Assert.False(download.SupportsRanges);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
        Assert.Equal(1, server.MaxConcurrent);
    }

    [Fact]
    public async Task Ftp_SessionLimit_OtherConnectionsFinishTheFile()
    {
        await using var server = TestFtpServer.Start(4 * 1024 * 1024);
        server.SessionLimit = 2;
        server.ChunkDelayMs = 1;

        var download = Download.Create(Request(server.FileUri), FastOptions(connections: 6));
        await download.RunAsync();

        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task Ftp_CancelAndResume_UsesRestAndMatches()
    {
        await using var server = TestFtpServer.Start(6 * 1024 * 1024);
        server.ChunkDelayMs = 4;

        var download = Download.Create(Request(server.FileUri), FastOptions());
        using var cts = new CancellationTokenSource();
        var run = download.RunAsync(cts.Token);
        var deadline = Stopwatch.StartNew();
        while (download.GetProgress().Downloaded < server.Size / 3 && !run.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(10);
        Assert.False(run.IsCompleted);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        server.ChunkDelayMs = 0;
        var resumed = Download.Resume(DownloadStateStore.StatePathOf(download.FilePath!), FastOptions());
        await resumed.RunAsync();
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(resumed.FilePath!));
    }

    [Fact]
    public async Task Ftp_Resume_WhenServerFileChanged_Throws()
    {
        await using var server = TestFtpServer.Start(6 * 1024 * 1024);
        server.ChunkDelayMs = 4;

        var download = Download.Create(Request(server.FileUri), FastOptions());
        using var cts = new CancellationTokenSource();
        var run = download.RunAsync(cts.Token);
        while (download.GetProgress().Downloaded < server.Size / 4 && !run.IsCompleted)
            await Task.Delay(10);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        server.Modified = server.Modified.AddHours(1);
        var resumed = Download.Resume(DownloadStateStore.StatePathOf(download.FilePath!), FastOptions());
        await Assert.ThrowsAsync<ServerFileChangedException>(() => resumed.RunAsync());
    }

    [Fact]
    public async Task Ftp_Login_UsesCredentialsFromUrl_AndRejectsWrongPassword()
    {
        await using var server = TestFtpServer.Start(64 * 1024);
        server.User = "tuna";
        server.Password = "geh eim";

        var good = new UriBuilder(server.FileUri) { UserName = "tuna", Password = "geh%20eim" }.Uri;
        var info = await Download.ProbeAsync(Request(good));
        Assert.Equal(64 * 1024, info.Size);
        Assert.True(info.SupportsRanges);
        Assert.Equal("test.bin", info.FileName);
        Assert.DoesNotContain(server.Commands, c => c.Contains("geh eim"));

        var bad = new UriBuilder(server.FileUri) { UserName = "tuna", Password = "falsch" }.Uri;
        var error = await Assert.ThrowsAsync<FtpException>(() => Download.ProbeAsync(Request(bad)));
        Assert.Equal(530, error.Reply.Code);
        Assert.False(error.IsTransient);
    }

    [Fact]
    public async Task Ftp_MissingFile_IsPermanentError()
    {
        await using var server = TestFtpServer.Start(1024);
        var url = new Uri(server.FileUri, "/pub/fehlt.bin");
        var error = await Assert.ThrowsAsync<FtpException>(() => Download.Create(Request(url), FastOptions()).RunAsync());
        Assert.Equal(550, error.Reply.Code);
    }

    [Fact]
    public async Task Ftp_ChangeCheck_ComparesSizeAndDate()
    {
        await using var server = TestFtpServer.Start(1000);
        var request = Request(server.FileUri);
        var same = await Download.CheckForChangeAsync(request, null, server.Modified, 1000);
        Assert.False(same.Changed);

        server.Modified = server.Modified.AddMinutes(5);
        Assert.True((await Download.CheckForChangeAsync(request, null, same.LastModified, 1000)).Changed);
        server.Size = 2000;
        var bigger = await Download.CheckForChangeAsync(request, null, server.Modified, 1000);
        Assert.True(bigger.Changed);
        Assert.Equal(2000, bigger.Size);
    }

    [Theory]
    [InlineData(TestFtpServer.TlsMode.Explicit)]
    [InlineData(TestFtpServer.TlsMode.Implicit)]
    public async Task Ftps_SeveralEncryptedConnections_ContentMatches(TestFtpServer.TlsMode mode)
    {
        FtpConnection.CertificateValidation = (_, _, _, _) => true; // selbst signierter Testserver
        await using var server = TestFtpServer.Start(3 * 1024 * 1024, mode);
        server.ChunkDelayMs = 2;

        var download = Download.Create(Request(server.FileUri), FastOptions());
        await download.RunAsync();

        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
        Assert.True(server.MaxConcurrent > 1, "nur eine Verbindung");
        Assert.Contains("PROT P", server.Commands);
        Assert.Equal(mode == TestFtpServer.TlsMode.Explicit, server.Commands.Contains("AUTH TLS"));
    }

    [Fact]
    public async Task Ftps_UntrustedCertificate_FailsWithClearError()
    {
        FtpConnection.CertificateValidation = null;
        await using var server = TestFtpServer.Start(1024, TestFtpServer.TlsMode.Explicit);
        var error = await Assert.ThrowsAsync<DownloadException>(() => Download.ProbeAsync(Request(server.FileUri)));
        Assert.StartsWith("FTPS", error.Message);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("USER") || c.StartsWith("PASS"));
    }

    [Theory]
    [InlineData("ftp://h/a", true)]
    [InlineData("ftps://h/a", true)]
    [InlineData("ftpes://u:p@h:2121/a", true)]
    [InlineData("https://h/a", true)]
    [InlineData("sftp://h/a", false)]
    [InlineData("file:///c:/a", false)]
    public void DownloadSchemes_KnowsFtpsVariants(string url, bool supported) =>
        Assert.Equal(supported, DownloadSchemes.IsSupported(new Uri(url)));

    [Fact]
    public void Credentials_WorkForFtpsUrls() =>
        Assert.Equal(("me", "pw"), FtpConnection.Credentials(new Uri("ftpes://me:pw@example.com:2121/a")));

    [Theory]
    [InlineData("20261001120000", "2026-10-01T12:00:00Z")]
    [InlineData("20261001120000.123", "2026-10-01T12:00:00Z")]
    public void ParseMdtm_ReadsUtc(string text, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), FtpConnection.ParseMdtm(text));

    [Fact]
    public void Credentials_DefaultToAnonymous()
    {
        Assert.Equal(("anonymous", "anonymous@"), FtpConnection.Credentials(new Uri("ftp://example.com/a")));
        Assert.Equal(("me", "p:w"), FtpConnection.Credentials(new Uri("ftp://me:p%3Aw@example.com/a")));
    }
}

public sealed class UrlPasswordTests
{
    [Fact]
    public void Password_IsMasked_Stripped_AndEncryptedAtRest()
    {
        const string url = "ftp://tuna:geheim@files.example.com/pub/a.iso";
        Assert.True(Secrets.HasPassword(url));
        Assert.Equal("ftp://tuna:***@files.example.com/pub/a.iso", Secrets.MaskPassword(url));
        Assert.Equal("ftp://tuna@files.example.com/pub/a.iso", Secrets.WithoutPassword(url));

        string stored = Secrets.ProtectUrl(url);
        Assert.DoesNotContain("geheim", stored);
        Assert.Equal(url, Secrets.UnprotectUrl(stored));

        // Ohne Passwort bleibt alles lesbar und unverändert.
        const string plain = "https://example.com/a.zip";
        Assert.False(Secrets.HasPassword(plain));
        Assert.Equal(plain, Secrets.ProtectUrl(plain));
        Assert.Equal(plain, Secrets.MaskPassword(plain));
        Assert.False(Secrets.HasPassword("ftp://nur-name@example.com/a"));

        string ef2 = Management.LinkList.WriteEf2([new Management.DownloadItem { Url = url }]);
        Assert.DoesNotContain("geheim", ef2);
    }
}
