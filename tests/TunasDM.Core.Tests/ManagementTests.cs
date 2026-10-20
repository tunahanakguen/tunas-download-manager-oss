using System.Diagnostics;
using TunasDM.Core.Management;

namespace TunasDM.Core.Tests;

public sealed class ManagementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public ManagementTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private DownloadDatabase NewDatabase() => new(Path.Combine(_directory, "test.db"));

    private AppSettings Settings() => new() { DownloadsFolder = Path.Combine(_directory, "Downloads"), MaxConnections = 4 };

    [Fact]
    public void Database_RoundTripsItemsAndSettings()
    {
        var database = NewDatabase();
        var item = new DownloadItem
        {
            Url = "https://example.com/a.zip", FileName = "a.zip", Directory = @"C:\x", Category = "Archive",
            Status = DownloadStatus.Paused, TotalSize = 1234, Downloaded = 100, SupportsRanges = true,
            Referrer = "https://example.com/", Description = "Test",
        };
        database.Insert(item);
        item.Downloaded = 200;
        database.Update(item);

        var loaded = Assert.Single(database.LoadAll());
        Assert.Equal(item.Id, loaded.Id);
        Assert.Equal((200L, 1234L, "Archive", DownloadStatus.Paused, true), (loaded.Downloaded, loaded.TotalSize, loaded.Category, loaded.Status, loaded.SupportsRanges));
        Assert.Equal(item.AddedAt, loaded.AddedAt);

        var settings = new AppSettings { MaxConnections = 5, SpeedLimit = 1000 };
        settings.Categories[0].Folder = @"D:\Eigene";
        database.SaveSettings(settings);
        var loadedSettings = database.LoadSettings();
        Assert.Equal((5, 1000L, @"D:\Eigene"), (loadedSettings.MaxConnections, loadedSettings.SpeedLimit, loadedSettings.Categories[0].Folder));

        database.Delete(item.Id);
        Assert.Empty(database.LoadAll());
    }

    [Theory]
    [InlineData("film.MP4", "Video")]
    [InlineData("setup.exe", "Programme")]
    [InlineData("daten.tar.gz", "Archive")]
    [InlineData("unbekannt.xyz", "Allgemein")]
    [InlineData("ohne-endung", "Allgemein")]
    public void Category_IsChosenByExtension(string fileName, string expected)
    {
        Assert.Equal(expected, Category.For(fileName, Category.Defaults()).Name);
    }

    [Fact]
    public void Category_FolderDefaultsToSubfolder()
    {
        var categories = Category.Defaults();
        Assert.Equal(@"C:\D", Category.For("x.bin", categories).ResolveFolder(@"C:\D"));
        Assert.Equal(@"C:\D\Video", Category.For("x.mkv", categories).ResolveFolder(@"C:\D"));
    }

    [Fact]
    public void CommandLine_ParsesIdmStyleArguments()
    {
        var request = CommandLineRequest.Parse(["/d", "https://example.com/f.zip", "/p", @"C:\Ziel", "/f", "neu.zip", "/a", "/n", "/q"]);

        Assert.Equal("https://example.com/f.zip", request.Url!.ToString());
        Assert.Equal((@"C:\Ziel", "neu.zip", true, true, true, false), (request.Directory, request.FileName, request.AddOnly, request.Silent, request.QuitAfter, request.StartQueue));
        Assert.Equal(request, CommandLineRequest.Parse(request.ToArguments()));
    }

    [Fact]
    public void CommandLine_AcceptsBareUrlAndQueueStart()
    {
        Assert.Equal("https://example.com/x", CommandLineRequest.Parse(["https://example.com/x"]).Url!.ToString());
        Assert.True(CommandLineRequest.Parse(["-s"]).StartQueue);
        Assert.True(CommandLineRequest.Parse([]).IsEmpty);
        Assert.Null(CommandLineRequest.Parse(["/d", "kein-link"]).Url);
    }

    [Fact]
    public async Task Manager_DownloadsIntoCategoryFolder_AndReportsCompletion()
    {
        await using var server = await TestFileServer.StartAsync(2 * 1024 * 1024);
        server.ContentDisposition = "attachment; filename=\"clip.mp4\"";
        var database = NewDatabase();
        await using var manager = new DownloadManager(database, Settings());
        var completed = new TaskCompletionSource<DownloadItem>();
        manager.ItemCompleted += (_, item) => completed.TrySetResult(item);

        var item = manager.Add(new NewDownload(server.FileUri) { FileName = "clip.mp4" }, startNow: true);
        var done = await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(item, done);
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(Path.Combine(_directory, "Downloads", "Video", "clip.mp4"), item.FilePath);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(item.FilePath!));
        Assert.Equal(DownloadStatus.Completed, Assert.Single(database.LoadAll()).Status);
    }

    [Fact]
    public async Task Manager_WithoutGivenName_UsesServerName()
    {
        await using var server = await TestFileServer.StartAsync(100_000);
        server.ContentDisposition = "attachment; filename=\"vom-server.zip\"";
        await using var manager = new DownloadManager(NewDatabase(), Settings());
        var completed = new TaskCompletionSource();
        manager.ItemCompleted += (_, _) => completed.TrySetResult();

        var item = manager.Add(new NewDownload(server.FileUri), startNow: true);
        Assert.Equal("test.bin", item.DisplayName);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("vom-server.zip", item.DisplayName);
        Assert.Equal("vom-server.zip", Path.GetFileName(item.FilePath));
    }

    [Fact]
    public async Task Manager_PauseAndStart_Resumes()
    {
        await using var server = await TestFileServer.StartAsync(6 * 1024 * 1024);
        server.ChunkDelayMs = 3;
        await using var manager = new DownloadManager(NewDatabase(), Settings());
        var completed = new TaskCompletionSource();
        manager.ItemCompleted += (_, _) => completed.TrySetResult();

        var item = manager.Add(new NewDownload(server.FileUri), startNow: true);
        await WaitFor(() => item.Progress.Downloaded > server.Size / 4);
        manager.Pause(item);
        await WaitFor(() => item.Status == DownloadStatus.Paused);

        Assert.True(item.Downloaded > 0);
        Assert.True(File.Exists(DownloadStateStore.StatePathOf(item.FilePath!)));

        server.ChunkDelayMs = 0;
        manager.Start(item);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(item.FilePath!));
    }

    [Fact]
    public async Task Manager_Queue_RespectsParallelLimit()
    {
        await using var server = await TestFileServer.StartAsync(1024 * 1024);
        server.ChunkDelayMs = 2;
        var settings = Settings();
        settings.MaxParallelDownloads = 1;
        await using var manager = new DownloadManager(NewDatabase(), settings);

        var items = Enumerable.Range(1, 3)
            .Select(i => manager.Add(new NewDownload(server.FileUri) { FileName = $"f{i}.bin" }, startNow: false))
            .ToList();
        Assert.All(items, i => Assert.Equal(DownloadStatus.Queued, i.Status));

        int maxActive = 0;
        manager.StartQueue();
        await WaitFor(() =>
        {
            maxActive = Math.Max(maxActive, items.Count(i => i.IsActive));
            return items.All(i => i.Status == DownloadStatus.Completed);
        });

        Assert.Equal(1, maxActive);
        Assert.False(manager.QueueRunning);
    }

    [Fact]
    public async Task Manager_Load_TurnsInterruptedDownloadsIntoPaused_WithSavedProgress()
    {
        var database = NewDatabase();
        string filePath = Path.Combine(_directory, "a.bin");
        DownloadStateStore.Save(DownloadStateStore.StatePathOf(filePath), new DownloadState
        {
            Url = "https://example.com/a", FilePath = filePath, TotalSize = 1000, SupportsRanges = true,
            Segments = [new SegmentState { Start = 0, Position = 300, End = 500 }, new SegmentState { Start = 500, Position = 600, End = 1000 }],
        });
        database.Insert(new DownloadItem { Url = "https://example.com/a", Directory = _directory, FilePath = filePath, Status = DownloadStatus.Downloading });

        await using var manager = new DownloadManager(database, Settings());
        manager.Load();

        var item = Assert.Single(manager.Items);
        Assert.Equal(DownloadStatus.Paused, item.Status);
        Assert.Equal(400, item.Downloaded);
        Assert.Equal(DownloadStatus.Paused, Assert.Single(database.LoadAll()).Status);
    }

    [Fact]
    public async Task Manager_Remove_DeletesPartialFiles()
    {
        await using var server = await TestFileServer.StartAsync(6 * 1024 * 1024);
        server.ChunkDelayMs = 3;
        await using var manager = new DownloadManager(NewDatabase(), Settings());

        var item = manager.Add(new NewDownload(server.FileUri), startNow: true);
        await WaitFor(() => item.Progress.Downloaded > 0 && item.FilePath is not null);
        string path = item.FilePath!;
        await manager.RemoveAsync(item, deleteFile: true);

        Assert.Empty(manager.Items);
        Assert.False(File.Exists(DownloadStateStore.TempPathOf(path)));
        Assert.False(File.Exists(DownloadStateStore.StatePathOf(path)));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("Bedingung nicht erreicht");
            await Task.Delay(10);
        }
    }
}
