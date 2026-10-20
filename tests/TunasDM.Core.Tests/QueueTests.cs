using Microsoft.Data.Sqlite;
using TunasDM.Core.Management;

namespace TunasDM.Core.Tests;

/// <summary>Mehrere Warteschlangen, Zeitplan, Wiederholungen und „fertig“-Meldung.</summary>
public sealed class QueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tunasdm-tests", Guid.NewGuid().ToString("N"));

    public QueueTests() => Directory.CreateDirectory(_directory);

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

    private string DatabasePath => Path.Combine(_directory, "q.db");

    private DownloadManager NewManager(int parallel = 3) =>
        new(new DownloadDatabase(DatabasePath), new AppSettings { DownloadsFolder = _directory, MaxConnections = 2, MaxParallelDownloads = parallel },
            Download.CreateHttpClient()) { RetryDelay = TimeSpan.FromMilliseconds(50) };

    private static async Task WaitFor(Func<bool> condition, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Bedingung nicht erfüllt.");
            await Task.Delay(20);
        }
    }

    // ---------- Zeitplan ----------

    private static readonly DateTime Monday = new(2026, 10, 19, 12, 0, 0); // ein Montag

    [Fact]
    public void Daily_StartsTodayOrTomorrow()
    {
        var schedule = new QueueSchedule { StartEnabled = true, Repeat = QueueRepeat.Daily, StartTime = new(23, 0, 0) };
        Assert.Equal(Monday.Date.AddHours(23), schedule.NextStart(Monday));
        Assert.Equal(Monday.Date.AddDays(1).AddHours(23), schedule.NextStart(Monday.Date.AddHours(23)));
    }

    [Fact]
    public void Weekdays_SkipsUnselectedDays()
    {
        var schedule = new QueueSchedule
        {
            StartEnabled = true, Repeat = QueueRepeat.Weekdays, StartTime = new(2, 0, 0),
            Days = [DayOfWeek.Saturday, DayOfWeek.Sunday],
        };
        Assert.Equal(new DateTime(2026, 10, 24, 2, 0, 0), schedule.NextStart(Monday));
        Assert.Equal(new DateTime(2026, 10, 25, 2, 0, 0), schedule.NextStart(new DateTime(2026, 10, 24, 2, 0, 0)));
        Assert.Null(new QueueSchedule { StartEnabled = true, Repeat = QueueRepeat.Weekdays, Days = [] }.NextStart(Monday));
    }

    [Fact]
    public void Once_OnlyBeforeItsTime_AndDisabledNever()
    {
        var schedule = new QueueSchedule { StartEnabled = true, Repeat = QueueRepeat.Once, Date = Monday.Date, StartTime = new(18, 30, 0) };
        Assert.Equal(Monday.Date.AddHours(18.5), schedule.NextStart(Monday));
        Assert.Null(schedule.NextStart(Monday.Date.AddHours(19)));
        Assert.Null(new QueueSchedule { StartEnabled = false }.NextStart(Monday));
    }

    [Fact]
    public void StopTime_IsNextOccurrenceAfterStart_AcrossMidnight()
    {
        var schedule = new QueueSchedule { StopEnabled = true, StopTime = new(7, 0, 0) };
        Assert.Equal(Monday.Date.AddDays(1).AddHours(7), schedule.StopAfter(Monday.Date.AddHours(23)));
        Assert.Equal(Monday.Date.AddHours(7), schedule.StopAfter(Monday.Date.AddHours(1)));
        Assert.Null(new QueueSchedule().StopAfter(Monday));
    }

    // ---------- Warteschlangen ----------

    [Fact]
    public async Task Queues_ArePersisted_MainFollowsSettings_AndRemovedQueueHandsItemsToMain()
    {
        long nightId;
        await using (var manager = NewManager(parallel: 2))
        {
            var night = manager.AddQueue(" Nachts ");
            night.MaxParallel = 1;
            night.After = AfterQueueAction.Shutdown;
            night.Schedule.StartEnabled = true;
            night.Schedule.StartTime = new(1, 30, 0);
            manager.UpdateQueue(night);
            nightId = night.Id;
            manager.Add(new NewDownload(new Uri("https://example.com/a.zip")) { QueueId = night.Id }, startNow: false);
        }

        await using (var manager = NewManager(parallel: 4))
        {
            manager.Load();
            Assert.Equal(["Hauptwarteschlange", "Nachts"], manager.Queues.Select(q => q.Name));
            Assert.Equal(4, manager.MainQueue.MaxParallel);
            var night = manager.Queues.Single(q => q.Id == nightId);
            Assert.Equal((1, AfterQueueAction.Shutdown, true, new TimeSpan(1, 30, 0)),
                (night.MaxParallel, night.After, night.Schedule.StartEnabled, night.Schedule.StartTime));
            var item = Assert.Single(manager.Items);
            Assert.Equal(nightId, item.QueueId);

            manager.RemoveQueue(night);
            Assert.Equal(DownloadQueue.MainId, item.QueueId);
            Assert.Throws<InvalidOperationException>(() => manager.RemoveQueue(manager.MainQueue));
        }
        await using (var manager = NewManager())
        {
            manager.Load();
            Assert.Equal(DownloadQueue.MainId, Assert.Single(manager.Items).QueueId);
            Assert.Single(manager.Queues);
        }
    }

    [Fact]
    public async Task StartingOneQueue_LeavesOtherQueuesWaiting_AndReportsFinished()
    {
        await using var server = await TestFileServer.StartAsync(256 * 1024);
        await using var manager = NewManager();
        var other = manager.AddQueue("Andere");
        var mine = manager.AddQueue("Meine");
        var waiting = manager.Add(new NewDownload(server.FileUri) { FileName = "andere.bin", QueueId = other.Id }, startNow: false);
        var items = Enumerable.Range(1, 2)
            .Select(i => manager.Add(new NewDownload(server.FileUri) { FileName = $"m{i}.bin", QueueId = mine.Id }, startNow: false))
            .ToList();
        var finished = new TaskCompletionSource<QueueFinishedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.QueueFinished += (_, e) => finished.TrySetResult(e);

        manager.StartQueue(mine);
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Same(mine, result.Queue);
        Assert.True(result.Downloaded);
        Assert.All(items, i => Assert.Equal(DownloadStatus.Completed, i.Status));
        Assert.All(items, i => Assert.True(i.StartedByQueue));
        Assert.Equal(DownloadStatus.Queued, waiting.Status);
        Assert.False(manager.IsRunning(mine));
    }

    [Fact]
    public async Task EmptyQueue_FinishesWithoutDownloads()
    {
        await using var manager = NewManager();
        QueueFinishedEventArgs? result = null;
        manager.QueueFinished += (_, e) => result = e;

        manager.StartQueue();

        Assert.NotNull(result);
        Assert.False(result.Downloaded);
        Assert.False(manager.QueueRunning);
    }

    [Fact]
    public async Task QueueParallelLimit_AppliesPerQueue()
    {
        await using var server = await TestFileServer.StartAsync(512 * 1024);
        server.ChunkDelayMs = 2;
        await using var manager = NewManager(parallel: 3);
        var single = manager.AddQueue("Einzeln");
        single.MaxParallel = 1;
        manager.UpdateQueue(single);
        var items = Enumerable.Range(1, 3)
            .Select(i => manager.Add(new NewDownload(server.FileUri) { FileName = $"e{i}.bin", QueueId = single.Id }, startNow: false))
            .ToList();

        int maxActive = 0;
        manager.StartQueue(single);
        await WaitFor(() =>
        {
            maxActive = Math.Max(maxActive, items.Count(i => i.IsActive));
            return items.All(i => i.Status == DownloadStatus.Completed);
        });
        Assert.Equal(1, maxActive);
    }

    [Fact]
    public async Task FailedDownload_IsRetried_ThenReportedOnce()
    {
        await using var server = await TestFileServer.StartAsync(64 * 1024);
        server.StatusOverride = 403;
        await using var manager = NewManager();
        manager.MainQueue.Retries = 2;
        int failures = 0;
        manager.ItemFailed += (_, _) => Interlocked.Increment(ref failures);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.QueueFinished += (_, _) => finished.TrySetResult();
        var item = manager.Add(new NewDownload(server.FileUri) { FileName = "x.bin" }, startNow: false);

        manager.StartQueue();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DownloadStatus.Error, item.Status);
        Assert.Equal(1, failures);
        Assert.Equal(3, server.CookieHeaders.Count); // erster Versuch + 2 Wiederholungen
    }

    [Fact]
    public async Task Scheduler_StartsAtStartTime_AndStopsAtStopTime()
    {
        await using var server = await TestFileServer.StartAsync(8 * 1024 * 1024);
        server.ChunkDelayMs = 20; // lädt lange genug, um gestoppt zu werden
        await using var manager = NewManager();
        var queue = manager.MainQueue;
        queue.Schedule = new QueueSchedule
        {
            StartEnabled = true, Repeat = QueueRepeat.Daily, StartTime = new(23, 0, 0),
            StopEnabled = true, StopTime = new(6, 0, 0),
        };
        manager.UpdateQueue(queue);
        var item = manager.Add(new NewDownload(server.FileUri) { FileName = "nacht.bin" }, startNow: false);
        var scheduler = new QueueScheduler(manager, Monday.Date.AddHours(22));

        scheduler.Tick(Monday.Date.AddHours(22.5));
        Assert.False(manager.QueueRunning);

        scheduler.Tick(Monday.Date.AddHours(23).AddSeconds(5));
        Assert.True(manager.IsRunning(queue));
        await WaitFor(() => item.IsActive);

        scheduler.Tick(Monday.Date.AddDays(1).AddHours(5));
        Assert.True(manager.IsRunning(queue));

        scheduler.Tick(Monday.Date.AddDays(1).AddHours(6).AddSeconds(5));
        Assert.False(manager.IsRunning(queue));
        await WaitFor(() => !item.IsActive);
        Assert.Equal(DownloadStatus.Queued, item.Status); // wartet wieder in der Warteschlange
    }

    // ---------- Sync ----------

    [Fact]
    public async Task CheckForChange_ComparesETagAndSize()
    {
        await using var server = await TestFileServer.StartAsync(128 * 1024);
        var request = new DownloadRequest(server.FileUri);

        var first = await Download.CheckForChangeAsync(request, null, null, server.Size);
        Assert.False(first.Changed);
        Assert.Equal("\"v1\"", first.ETag);

        Assert.False((await Download.CheckForChangeAsync(request, "\"v1\"", null, server.Size)).Changed);
        server.Version = "v2";
        Assert.True((await Download.CheckForChangeAsync(request, "\"v1\"", null, server.Size)).Changed);
        // Ohne Kennung zählt nur die Größe.
        Assert.True((await Download.CheckForChangeAsync(request, null, null, server.Size + 1)).Changed);
    }

    [Fact]
    public async Task ReplaceExisting_OverwritesSameNameAtTheEnd()
    {
        await using var server = await TestFileServer.StartAsync(256 * 1024);
        string target = Path.Combine(_directory, "daten.bin");
        await File.WriteAllTextAsync(target, "alte Fassung");

        var download = Download.Create(new DownloadRequest(server.FileUri) { TargetDirectory = _directory, FileName = "daten.bin", ReplaceExisting = true });
        await download.RunAsync();

        Assert.Equal(target, download.FilePath);
        Assert.Equal(server.Expected(), await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(Path.Combine(_directory, "daten (2).bin")));
    }

    [Fact]
    public async Task SyncQueue_ReloadsChangedFiles_InPlace_AndSkipsUnchanged()
    {
        await using var server = await TestFileServer.StartAsync(256 * 1024);
        await using var manager = NewManager();
        var sync = manager.AddQueue("Sync");
        sync.Sync = true;
        manager.UpdateQueue(sync);
        var item = manager.Add(new NewDownload(server.FileUri) { FileName = "liste.bin", QueueId = sync.Id }, startNow: false);
        var results = new List<SyncCheckedEventArgs>();
        manager.SyncChecked += (_, e) => { lock (results) results.Add(e); };

        async Task RunQueue()
        {
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFinished(object? s, QueueFinishedEventArgs e) => finished.TrySetResult();
            manager.QueueFinished += OnFinished;
            manager.StartQueue(sync);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            manager.QueueFinished -= OnFinished;
        }

        await RunQueue();
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal("\"v1\"", item.ETag);
        string path = item.FilePath!;

        // Unverändert: nur eine Prüfanfrage, kein neuer Download.
        int before = server.RequestCount;
        await RunQueue();
        Assert.Equal(before + 1, server.RequestCount);
        Assert.Equal((1, 0), (results[^1].Checked, results[^1].Changed));

        // Geändert: neu geladen, dieselbe Datei ersetzt.
        server.Version = "v2";
        await RunQueue();
        Assert.Equal((1, 1), (results[^1].Checked, results[^1].Changed));
        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(path, item.FilePath);
        Assert.Equal("\"v2\"", item.ETag);
        Assert.False(item.ReplaceOnComplete);
        Assert.Equal(server.Expected("v2"), await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(_directory, "liste*"));
    }
}
