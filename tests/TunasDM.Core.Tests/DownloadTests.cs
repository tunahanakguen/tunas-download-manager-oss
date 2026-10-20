using System.Diagnostics;

namespace TunasDM.Core.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public DownloadTests() => Directory.CreateDirectory(_directory);

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
    };

    private DownloadRequest Request(TestFileServer server, string? fileName = null) =>
        new(server.FileUri) { TargetDirectory = _directory, FileName = fileName };

    private void AssertOnlyFinalFile(string path)
    {
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task WithRanges_UsesSeveralConnections_AndContentMatches()
    {
        await using var server = await TestFileServer.StartAsync(8 * 1024 * 1024);
        server.ChunkDelayMs = 1;

        var download = Download.Create(Request(server), FastOptions());
        await download.RunAsync();

        Assert.True(download.SupportsRanges);
        Assert.True(server.MaxConcurrent >= 2, $"nur {server.MaxConcurrent} gleichzeitige Verbindung(en)");
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
        AssertOnlyFinalFile(download.FilePath!);
    }

    [Fact]
    public async Task WithoutRanges_UsesSingleConnection()
    {
        await using var server = await TestFileServer.StartAsync(2 * 1024 * 1024);
        server.SupportsRanges = false;

        var download = Download.Create(Request(server), FastOptions());
        await download.RunAsync();

        Assert.False(download.SupportsRanges);
        Assert.Equal(1, server.MaxConcurrent);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task UnknownSize_IsDownloadedToTheEnd()
    {
        await using var server = await TestFileServer.StartAsync(700_000);
        server.SupportsRanges = false;
        server.SendContentLength = false;

        var download = Download.Create(Request(server), FastOptions());
        await download.RunAsync();

        Assert.Equal(server.Size, download.TotalSize);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task Cancel_ThenResume_CompletesWithCorrectContent()
    {
        await using var server = await TestFileServer.StartAsync(6 * 1024 * 1024);
        server.ChunkDelayMs = 3;

        var download = Download.Create(Request(server), FastOptions());
        string statePath = await RunUntilAndCancel(download, server.Size / 3);

        Assert.True(File.Exists(statePath));
        long savedBytes = DownloadStateStore.Load(statePath).Segments.Sum(s => s.Position - s.Start);
        Assert.True(savedBytes > 0);

        server.ChunkDelayMs = 0;
        int requestsBefore = server.RequestCount;
        var resumed = Download.Resume(statePath, FastOptions());
        await resumed.RunAsync();

        Assert.True(server.RequestCount > requestsBefore);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(resumed.FilePath!));
        AssertOnlyFinalFile(resumed.FilePath!);
    }

    [Fact]
    public async Task Resume_WhenServerFileChanged_Throws()
    {
        await using var server = await TestFileServer.StartAsync(6 * 1024 * 1024);
        server.ChunkDelayMs = 3;

        var download = Download.Create(Request(server), FastOptions());
        string statePath = await RunUntilAndCancel(download, server.Size / 3);

        server.Version = "v2";
        var resumed = Download.Resume(statePath, FastOptions());

        await Assert.ThrowsAsync<ServerFileChangedException>(() => resumed.RunAsync());
        Assert.True(File.Exists(statePath), "Stand muss für eine Entscheidung erhalten bleiben");
    }

    [Fact]
    public async Task DroppedConnections_AreRetried()
    {
        await using var server = await TestFileServer.StartAsync(3 * 1024 * 1024);
        server.DropNext(count: 3, afterBytes: 200_000);

        var download = Download.Create(Request(server), FastOptions());
        await download.RunAsync();

        Assert.True(server.RequestCount >= 4);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task SlowConnection_IsDroppedAndReplaced()
    {
        await using var server = await TestFileServer.StartAsync(16 * 1024 * 1024);
        server.ChunkDelayMs = 5;
        server.SlowRequests.Add(2); // erste Teilung: ca. 50 KB/s statt ca. 3 MB/s

        var options = FastOptions(connections: 3);
        options.SlowConnectionGrace = TimeSpan.FromSeconds(1);
        var download = Download.Create(Request(server), options);
        await download.RunAsync();

        Assert.True(download.SlowConnectionsDropped >= 1, "langsame Verbindung wurde nicht ersetzt");
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task EvenConnections_AreNotDropped()
    {
        await using var server = await TestFileServer.StartAsync(8 * 1024 * 1024);
        server.ChunkDelayMs = 5;

        var options = FastOptions(connections: 3);
        options.SlowConnectionGrace = TimeSpan.FromSeconds(1);
        var download = Download.Create(Request(server), options);
        await download.RunAsync();

        Assert.Equal(0, download.SlowConnectionsDropped);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task FileName_ComesFromContentDisposition_AndIsMadeUnique()
    {
        await using var server = await TestFileServer.StartAsync(10_000);
        server.ContentDisposition = "attachment; filename=\"report.pdf\"";
        await File.WriteAllTextAsync(Path.Combine(_directory, "report.pdf"), "alt");

        var download = Download.Create(Request(server), FastOptions());
        await download.RunAsync();

        Assert.Equal("report (1).pdf", Path.GetFileName(download.FilePath));
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task SpeedLimiter_SlowsDownTheDownload()
    {
        await using var server = await TestFileServer.StartAsync(1536 * 1024);
        var limiter = new SpeedLimiter(1024 * 1024);

        var watch = Stopwatch.StartNew();
        var download = Download.Create(Request(server), FastOptions(), limiter: limiter);
        await download.RunAsync();
        watch.Stop();

        // 1,5 MB bei 1 MB/s mit 0,25 MB Startguthaben: mindestens etwa 1,25 s.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1), $"zu schnell: {watch.Elapsed}");
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(download.FilePath!));
    }

    [Fact]
    public async Task FindByUrl_FindsUnfinishedDownload()
    {
        await using var server = await TestFileServer.StartAsync(4 * 1024 * 1024);
        server.ChunkDelayMs = 3;

        var download = Download.Create(Request(server), FastOptions());
        string statePath = await RunUntilAndCancel(download, server.Size / 4);

        Assert.Equal(statePath, DownloadStateStore.FindByUrl(_directory, server.FileUri));
    }

    private static async Task<string> RunUntilAndCancel(Download download, long bytes)
    {
        using var cts = new CancellationTokenSource();
        var run = download.RunAsync(cts.Token);
        var deadline = Stopwatch.StartNew();
        while (download.GetProgress().Downloaded < bytes && !run.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(20))
            await Task.Delay(10);

        Assert.False(run.IsCompleted, "Download war schon fertig, bevor abgebrochen werden konnte");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        return DownloadStateStore.StatePathOf(download.FilePath!);
    }
}
