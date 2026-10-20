using TunasDM.Core;

namespace TunasDM.Core.Management;

/// <summary>Was zum Hinzufügen eines Downloads bekannt ist (aus Dialog, Kommandozeile oder Browser).</summary>
public sealed record NewDownload(Uri Url)
{
    public string? FileName { get; init; }

    /// <summary>Zielordner; leer = Ordner der Kategorie.</summary>
    public string? Directory { get; init; }

    public string? Category { get; init; }
    public string? Referrer { get; init; }
    public string? Cookies { get; init; }
    public string? UserAgent { get; init; }
    public string? Description { get; init; }
    public long KnownSize { get; init; } = -1;

    /// <summary>Media = über yt-dlp (Video-Seite oder Stream), mit <see cref="MediaFormat"/> als Qualität.</summary>
    public DownloadKind Kind { get; init; }

    public string? MediaFormat { get; init; }

    /// <summary>Warteschlange für „Später laden“; leer = Hauptwarteschlange.</summary>
    public long? QueueId { get; init; }
}

public sealed class SyncCheckedEventArgs(DownloadQueue queue, int checkedFiles, int changed, int failed) : EventArgs
{
    public DownloadQueue Queue { get; } = queue;
    public int Checked { get; } = checkedFiles;
    public int Changed { get; } = changed;
    public int Failed { get; } = failed;
}

public sealed class QueueFinishedEventArgs(DownloadQueue queue, bool downloaded) : EventArgs
{
    public DownloadQueue Queue { get; } = queue;
    public bool Downloaded { get; } = downloaded;
}

/// <summary>
/// Verwaltet die Download-Liste: hinzufügen, starten, pausieren, löschen, Warteschlange.
/// Ereignisse kommen aus Hintergrund-Threads; die Oberfläche muss selbst auf den UI-Thread wechseln.
/// </summary>
public sealed class DownloadManager : IAsyncDisposable
{
    private readonly DownloadDatabase _database;
    private readonly HttpClient? _client;
    private readonly List<DownloadItem> _items = new();
    private readonly object _lock = new();
    private readonly List<DownloadQueue> _queues;

    /// <summary>Download-Id → Warteschlange, die ihn gestartet hat.</summary>
    private readonly Dictionary<long, long> _queueStarted = new();

    /// <summary>Laufende Warteschlangen → wie viele Downloads sie in diesem Lauf gestartet haben.</summary>
    private readonly Dictionary<long, int> _runs = new();

    /// <summary>Download-Id → bisherige Wiederholungen nach Fehlern in der Warteschlange.</summary>
    private readonly Dictionary<long, int> _retries = new();

    private readonly HashSet<long> _stoppedByQueue = new();
    private ProxySettings? _proxy;

    public DownloadManager(DownloadDatabase database, AppSettings settings, HttpClient? client = null)
    {
        _database = database;
        _client = client;
        Settings = settings;
        Limiter = new SpeedLimiter(settings.SpeedLimit);
        ApplyProxy(settings);

        _queues = database.LoadQueues();
        if (_queues.FirstOrDefault(q => q.IsMain) is not { } main)
            _queues.Insert(0, main = DownloadQueue.CreateMain(settings.MaxParallelDownloads));
        // Die Einstellung „Gleichzeitige Downloads“ gehört zur Hauptwarteschlange.
        main.MaxParallel = Math.Max(1, settings.MaxParallelDownloads);
    }

    /// <summary>Proxy für neue Verbindungen (eigene Engine und yt-dlp). Mit eigenem Client (Tests) unverändert.</summary>
    private void ApplyProxy(AppSettings settings)
    {
        var proxy = settings.Proxy();
        if (_client is not null || proxy == _proxy)
            return;
        _proxy = proxy;
        Download.UseProxy(proxy);
        Media.YtDlp.Proxy = proxy.YtDlpArgument();
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Gemeinsames Limit für alle Downloads.</summary>
    public SpeedLimiter Limiter { get; }

    /// <summary>Läuft mindestens eine Warteschlange?</summary>
    public bool QueueRunning
    {
        get
        {
            lock (_lock)
                return _runs.Count > 0;
        }
    }

    public IReadOnlyList<DownloadQueue> Queues
    {
        get
        {
            lock (_lock)
                return _queues.ToList();
        }
    }

    public DownloadQueue MainQueue
    {
        get
        {
            lock (_lock)
                return _queues.First(q => q.IsMain);
        }
    }

    public DownloadQueue QueueOf(DownloadItem item)
    {
        lock (_lock)
            return _queues.FirstOrDefault(q => q.Id == item.QueueId) ?? _queues.First(q => q.IsMain);
    }

    public bool IsRunning(DownloadQueue queue)
    {
        lock (_lock)
            return _runs.ContainsKey(queue.Id);
    }

    /// <summary>
    /// Eine Warteschlange ist von selbst leer gelaufen (nicht angehalten). <c>Downloaded</c> = in diesem Lauf
    /// wurde mindestens ein Download gestartet; nur dann gilt die Aktion danach (z. B. Herunterfahren).
    /// </summary>
    public event EventHandler<QueueFinishedEventArgs>? QueueFinished;

    public event EventHandler<DownloadItem>? ItemAdded;
    public event EventHandler<DownloadItem>? ItemRemoved;
    public event EventHandler<DownloadItem>? ItemChanged;
    public event EventHandler<DownloadItem>? ItemCompleted;
    public event EventHandler<DownloadItem>? ItemFailed;
    public event EventHandler? QueueStateChanged;

    public IReadOnlyList<DownloadItem> Items
    {
        get
        {
            lock (_lock)
                return _items.ToList();
        }
    }

    /// <summary>Liste laden. Was beim letzten Beenden lief, steht danach auf „Pausiert“.</summary>
    public void Load()
    {
        var items = _database.LoadAll();
        foreach (var item in items.Where(i => i.Status != DownloadStatus.Completed))
        {
            // Nach einem Absturz steht der echte Stand nur in der Stand-Datei.
            if (item.FilePath is { } path && File.Exists(DownloadStateStore.StatePathOf(path)))
            {
                try
                {
                    item.Downloaded = DownloadStateStore.Load(DownloadStateStore.StatePathOf(path)).Segments.Sum(s => s.Position - s.Start);
                }
                catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or DownloadException)
                {
                    // Unlesbar: beim Start wird neu entschieden.
                }
            }
            if (item.IsActive)
                item.Status = DownloadStatus.Paused;
            _database.Update(item);
        }
        lock (_lock)
        {
            _items.Clear();
            _items.AddRange(items);
        }
    }

    /// <summary>Geänderte Einstellungen ohne Nebenwirkungen speichern (z. B. Zeitpunkt der Update-Suche).</summary>
    public void SaveSettings() => _database.SaveSettings(Settings);

    public void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        Limiter.BytesPerSecond = settings.SpeedLimit;
        ApplyProxy(settings);
        _database.SaveSettings(settings);
        lock (_lock)
            _queues.First(q => q.IsMain).MaxParallel = Math.Max(1, settings.MaxParallelDownloads);
        PumpQueue();
    }

    public DownloadQueue AddQueue(string name)
    {
        DownloadQueue queue;
        lock (_lock)
        {
            queue = new DownloadQueue { Id = _queues.Max(q => q.Id) + 1, Name = name.Trim() };
            _queues.Add(queue);
        }
        SaveQueues();
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
        return queue;
    }

    /// <summary>Name, Zeitplan, Parallelität oder Aktion danach geändert. Bei der Hauptwarteschlange gilt die Parallelität auch für die Einstellungen.</summary>
    public void UpdateQueue(DownloadQueue queue)
    {
        if (queue.IsMain && queue.MaxParallel != Settings.MaxParallelDownloads)
        {
            Settings.MaxParallelDownloads = Math.Max(1, queue.MaxParallel);
            _database.SaveSettings(Settings);
        }
        SaveQueues();
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
        PumpQueue();
    }

    /// <summary>Entfernt eine Warteschlange (nicht die Hauptwarteschlange); ihre Downloads wandern in die Hauptwarteschlange.</summary>
    public void RemoveQueue(DownloadQueue queue)
    {
        if (queue.IsMain)
            throw new InvalidOperationException(L.T("Die Hauptwarteschlange lässt sich nicht löschen."));
        StopQueue(queue);
        List<DownloadItem> moved;
        lock (_lock)
        {
            _queues.Remove(queue);
            moved = _items.Where(i => i.QueueId == queue.Id).ToList();
            foreach (var item in moved)
                item.QueueId = DownloadQueue.MainId;
        }
        foreach (var item in moved)
            Changed(item);
        SaveQueues();
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Download einer anderen Warteschlange zuordnen (wartet er gerade, dann ab jetzt dort).</summary>
    public void MoveToQueue(DownloadItem item, DownloadQueue queue)
    {
        lock (_lock)
            item.QueueId = queue.Id;
        Changed(item);
        PumpQueue();
    }

    private void SaveQueues()
    {
        List<DownloadQueue> queues;
        lock (_lock)
            queues = _queues.ToList();
        _database.SaveQueues(queues);
    }

    public DownloadItem Add(NewDownload request, bool startNow)
    {
        // Ohne vorgegebenen Namen entscheidet der Server (Content-Disposition); die URL dient nur als Vermutung.
        string? fixedName = request.FileName is { Length: > 0 } fileName ? FileNameHelper.Sanitize(fileName) : null;
        string name = fixedName ?? DownloadItem.NameFromUrl(request.Url.ToString());
        string category = request.Category ?? Category.For(name, Settings.Categories).Name;

        var item = new DownloadItem
        {
            Url = request.Url.ToString(),
            FileName = fixedName,
            Category = category,
            Directory = string.IsNullOrWhiteSpace(request.Directory) ? Settings.FolderFor(category) : request.Directory,
            Status = DownloadStatus.Queued,
            TotalSize = request.KnownSize,
            Referrer = request.Referrer,
            Cookies = request.Cookies,
            UserAgent = request.UserAgent,
            Description = request.Description,
            Kind = request.Kind,
            MediaFormat = request.MediaFormat,
            QueueId = request.QueueId is long queueId && Queues.Any(q => q.Id == queueId) ? queueId : DownloadQueue.MainId,
        };
        _database.Insert(item);
        lock (_lock)
            _items.Add(item);
        ItemAdded?.Invoke(this, item);

        if (startNow)
            Start(item);
        else
            PumpQueue();
        return item;
    }

    /// <summary>Eintrag mit derselben Adresse (für den Duplikat-Dialog), bevorzugt einer, der noch nicht fertig ist.</summary>
    public DownloadItem? FindDuplicate(Uri url)
    {
        string text = url.ToString();
        lock (_lock)
            return _items.Where(i => i.Url == text).OrderBy(i => i.Status == DownloadStatus.Completed).FirstOrDefault();
    }

    /// <summary>
    /// Neue Adresse für einen abgelaufenen Link („Adresse aktualisieren“). Der bisherige Stand bleibt erhalten;
    /// beim Fortsetzen prüft die Engine per ETag und Vergleichsbytes, dass es dieselbe Datei ist.
    /// </summary>
    public void UpdateAddress(DownloadItem item, Uri url, string? cookies, string? referrer, string? userAgent)
    {
        lock (_lock)
        {
            if (item.IsActive || item.Status == DownloadStatus.Completed)
                throw new InvalidOperationException(L.T("Die Adresse lässt sich nur bei pausierten oder fehlgeschlagenen Downloads ändern."));
            item.Url = url.ToString();
            item.Cookies = string.IsNullOrEmpty(cookies) ? null : cookies;
            item.Referrer = referrer ?? item.Referrer;
            item.UserAgent = userAgent ?? item.UserAgent;
            item.Error = null;
            if (item.Status == DownloadStatus.Error)
                item.Status = DownloadStatus.Paused;
        }

        if (item.FilePath is { } path && File.Exists(DownloadStateStore.StatePathOf(path)))
        {
            string statePath = DownloadStateStore.StatePathOf(path);
            var state = DownloadStateStore.Load(statePath);
            state.Url = item.Url;
            state.Cookies = item.Cookies;
            state.Referrer = item.Referrer;
            state.UserAgent = item.UserAgent;
            DownloadStateStore.Save(statePath, state);
        }
        Changed(item);
    }

    public void Start(DownloadItem item) => Start(item, byQueue: false);

    private void Start(DownloadItem item, bool byQueue)
    {
        lock (_lock)
        {
            if (item.IsActive || item.Status == DownloadStatus.Completed || !_items.Contains(item))
                return;
            item.StartedByQueue = byQueue;
            item.Status = DownloadStatus.Connecting;
            item.Error = null;
            item.Cancellation = new CancellationTokenSource();
            item.Download = CreateEngine(item);
            item.RunTask = RunAsync(item, item.Download, item.Cancellation.Token);
        }
        Changed(item);
    }

    public void Pause(DownloadItem item)
    {
        lock (_lock)
        {
            _queueStarted.Remove(item.Id);
            _stoppedByQueue.Remove(item.Id);
            if (item.IsActive)
            {
                item.Cancellation?.Cancel();
                return;
            }
            if (item.Status != DownloadStatus.Queued)
                return;
            item.Status = DownloadStatus.Paused;
        }
        Changed(item);
    }

    public void PauseAll()
    {
        StopQueue();
        foreach (var item in Items)
            Pause(item);
    }

    /// <summary>Pausierte oder fehlgeschlagene Downloads in die Warteschlange stellen (ohne Angabe: in ihre bisherige).</summary>
    public void Enqueue(DownloadItem item, DownloadQueue? queue = null)
    {
        lock (_lock)
        {
            if (queue is not null)
                item.QueueId = queue.Id;
            if (item.Status is not (DownloadStatus.Paused or DownloadStatus.Error or DownloadStatus.Queued))
                return;
            item.Status = DownloadStatus.Queued;
        }
        Changed(item);
        PumpQueue();
    }

    public async Task RemoveAsync(DownloadItem item, bool deleteFile)
    {
        Task? running;
        lock (_lock)
        {
            if (!_items.Remove(item))
                return;
            _queueStarted.Remove(item.Id);
            item.Cancellation?.Cancel();
            running = item.RunTask;
        }
        if (running is not null)
            await running.ConfigureAwait(false);

        DeletePartialFiles(item);
        if (deleteFile && item.Status == DownloadStatus.Completed && item.FilePath is { } path && File.Exists(path))
            File.Delete(path);

        _database.Delete(item.Id);
        ItemRemoved?.Invoke(this, item);
        PumpQueue();
    }

    /// <summary>Datei verwerfen und von vorne laden.</summary>
    public async Task RestartAsync(DownloadItem item)
    {
        Task? running;
        lock (_lock)
        {
            item.Cancellation?.Cancel();
            running = item.RunTask;
        }
        if (running is not null)
            await running.ConfigureAwait(false);

        DeletePartialFiles(item);
        lock (_lock)
        {
            item.Downloaded = 0;
            item.Status = DownloadStatus.Paused;
            item.CompletedAt = null;
            if (item.FilePath is not null)
                item.FileName = Path.GetFileName(item.FilePath);
            item.FilePath = null;
        }
        Start(item);
    }

    /// <summary>Hauptwarteschlange starten.</summary>
    public void StartQueue() => StartQueue(MainQueue);

    public void StartQueue(DownloadQueue queue)
    {
        lock (_lock)
        {
            if (_runs.ContainsKey(queue.Id) || !_queues.Contains(queue))
                return;
            _runs[queue.Id] = 0;
            // Neuer Lauf: Wiederholungen zählen von vorn.
            foreach (var item in _items.Where(i => i.QueueId == queue.Id))
                _retries.Remove(item.Id);
            if (queue.Sync)
                _syncing.Add(queue.Id);
        }
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
        if (queue.Sync)
            _ = SyncAsync(queue);
        else
            PumpQueue();
    }

    /// <summary>Warteschlangen, deren fertige Dateien gerade geprüft werden; sie gelten bis dahin nicht als fertig.</summary>
    private readonly HashSet<long> _syncing = new();

    /// <summary>Nach der Sync-Prüfung: so viele fertige Dateien geprüft, so viele geändert, so viele nicht prüfbar.</summary>
    public event EventHandler<SyncCheckedEventArgs>? SyncChecked;

    /// <summary>Fertige Dateien der Warteschlange prüfen; geänderte warten danach wieder in ihr.</summary>
    private async Task SyncAsync(DownloadQueue queue)
    {
        int checkedCount = 0, changed = 0, failed = 0;
        try
        {
            List<DownloadItem> done;
            lock (_lock)
                done = _items.Where(i => i.QueueId == queue.Id && i.Status == DownloadStatus.Completed && i.Kind == DownloadKind.File).ToList();

            foreach (var item in done)
            {
                if (!IsRunning(queue))
                    break;
                checkedCount++;
                try
                {
                    var request = new DownloadRequest(new Uri(item.Url))
                    {
                        Referrer = item.Referrer, Cookies = item.Cookies, UserAgent = item.UserAgent,
                    };
                    var result = await Download.CheckForChangeAsync(request, item.ETag, item.LastModified, item.TotalSize, _client)
                        .ConfigureAwait(false);
                    lock (_lock)
                    {
                        item.ETag ??= result.ETag;
                        item.LastModified ??= result.LastModified;
                        if (result.Changed && _items.Contains(item) && item.Status == DownloadStatus.Completed)
                        {
                            changed++;
                            // Neu laden unter demselben Namen; die vorhandene Datei bleibt, bis die neue fertig ist.
                            if (item.FilePath is { } path)
                            {
                                item.FileName = Path.GetFileName(path);
                                item.Directory = Path.GetDirectoryName(path)!;
                            }
                            item.FilePath = null;
                            item.ReplaceOnComplete = true;
                            item.Downloaded = 0;
                            item.TotalSize = result.Size;
                            item.CompletedAt = null;
                            item.Status = DownloadStatus.Queued;
                        }
                    }
                    Changed(item);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or OperationCanceledException
                    or DownloadException or UriFormatException)
                {
                    // Server nicht erreichbar oder Link abgelaufen: Datei bleibt, wie sie ist.
                    failed++;
                }
            }
        }
        finally
        {
            lock (_lock)
                _syncing.Remove(queue.Id);
            SyncChecked?.Invoke(this, new SyncCheckedEventArgs(queue, checkedCount, changed, failed));
            PumpQueue();
        }
    }

    /// <summary>Alle laufenden Warteschlangen anhalten.</summary>
    public void StopQueue()
    {
        foreach (var queue in Queues)
            StopQueue(queue);
    }

    public void StopQueue(DownloadQueue queue)
    {
        List<DownloadItem> started;
        lock (_lock)
        {
            if (!_runs.Remove(queue.Id))
                return;
            started = _items.Where(i => _queueStarted.TryGetValue(i.Id, out long q) && q == queue.Id && i.IsActive).ToList();
            foreach (var item in started)
            {
                _queueStarted.Remove(item.Id);
                _stoppedByQueue.Add(item.Id);
            }
        }
        // Was die Warteschlange gestartet hat, kommt zurück in die Warteschlange.
        foreach (var item in started)
            item.Cancellation?.Cancel();
        QueueStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gesamtgeschwindigkeit und Stand aller laufenden Downloads.</summary>
    public (int Active, long Downloaded, long Total) Summary()
    {
        var active = Items.Where(i => i.IsActive).Select(i => i.Progress).ToList();
        return (active.Count, active.Sum(p => p.Downloaded), active.Sum(p => Math.Max(0, p.TotalSize)));
    }

    private IDownloadJob CreateEngine(DownloadItem item)
    {
        if (item.Kind == DownloadKind.Media)
        {
            // Cookies bewusst nicht an yt-dlp: es lädt von CDN-Servern anderer Domains.
            string baseName = Path.GetFileNameWithoutExtension(item.FileName ?? DownloadItem.NameFromUrl(item.Url));
            return new Media.MediaDownload(item.Url, item.Directory, baseName, item.MediaFormat ?? Media.YtDlp.BestCode,
                item.Referrer, item.UserAgent, Limiter);
        }

        var options = Settings.ToDownloadOptions();
        string? statePath = item.FilePath is { } path ? DownloadStateStore.StatePathOf(path) : null;
        if (statePath is null || !File.Exists(statePath))
        {
            // Angefangene Datei derselben Adresse nur übernehmen, wenn sie keinem anderen Eintrag gehört
            // (Duplikat „unter neuem Namen laden“ startet sonst im Stand des ersten).
            statePath = DownloadStateStore.FindByUrl(item.Directory, new Uri(item.Url));
            lock (_lock)
            {
                if (statePath is not null && _items.Any(i => i != item && i.FilePath is { } other
                        && string.Equals(DownloadStateStore.StatePathOf(other), statePath, StringComparison.OrdinalIgnoreCase)))
                    statePath = null;
            }
        }

        if (statePath is not null)
            return Download.Resume(statePath, options, _client, Limiter);

        var request = new DownloadRequest(new Uri(item.Url))
        {
            TargetDirectory = item.Directory,
            FileName = item.FileName,
            Referrer = item.Referrer,
            Cookies = item.Cookies,
            UserAgent = item.UserAgent,
            ReplaceExisting = item.ReplaceOnComplete,
        };
        return Download.Create(request, options, _client, Limiter);
    }

    private async Task RunAsync(DownloadItem item, IDownloadJob download, CancellationToken token)
    {
        await Task.Yield();
        download.Prepared += (_, _) =>
        {
            // Bei Videos steht der endgültige Name (Endung) erst nach dem Zusammenfügen fest.
            if (download.FilePath is { } path)
            {
                item.FilePath = path;
                item.FileName = Path.GetFileName(path);
            }
            item.TotalSize = download.TotalSize;
            item.SupportsRanges = download.SupportsRanges;
            item.Status = DownloadStatus.Downloading;
            Changed(item);
        };

        DownloadStatus final;
        try
        {
            await download.RunAsync(token).ConfigureAwait(false);
            final = DownloadStatus.Completed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            final = DownloadStatus.Paused;
        }
        catch (Exception ex)
        {
            item.Error = IsExpiredLink(ex)
                ? ex.Message + " " + L.T("Der Link ist vermutlich abgelaufen: Rechtsklick → „Adresse aktualisieren“.")
                : ex.Message;
            final = DownloadStatus.Error;
        }

        bool requeue;
        lock (_lock)
        {
            var progress = download.GetProgress();
            item.FilePath = download.FilePath ?? item.FilePath;
            if (item.FilePath is not null)
                item.FileName = Path.GetFileName(item.FilePath);
            item.TotalSize = download.TotalSize >= 0 ? download.TotalSize : item.TotalSize;
            item.SupportsRanges = download.SupportsRanges;
            item.Downloaded = final == DownloadStatus.Completed ? Math.Max(item.TotalSize, progress.Downloaded) : progress.Downloaded;
            if (!item.SupportsRanges && final != DownloadStatus.Completed)
                item.Downloaded = 0;

            // Von der Warteschlange gestoppt (nicht vom Nutzer): wieder einreihen.
            requeue = _stoppedByQueue.Remove(item.Id) && final == DownloadStatus.Paused && _items.Contains(item);

            // Fehler in einer laufenden Warteschlange: bis zu „Retries“-mal erneut einreihen.
            if (final == DownloadStatus.Error && _queueStarted.TryGetValue(item.Id, out long queueId)
                && _runs.ContainsKey(queueId) && _queues.FirstOrDefault(q => q.Id == queueId) is { } queue
                && _items.Contains(item))
            {
                int tried = _retries.GetValueOrDefault(item.Id);
                if (tried < queue.Retries)
                {
                    _retries[item.Id] = tried + 1;
                    _retryAfter[item.Id] = DateTime.UtcNow + RetryDelay;
                    requeue = true;
                }
            }
            else if (final == DownloadStatus.Completed)
            {
                _retries.Remove(item.Id);
            }
            _queueStarted.Remove(item.Id);
            item.Status = requeue ? DownloadStatus.Queued : final;
            if (final == DownloadStatus.Completed)
            {
                item.CompletedAt = DateTimeOffset.Now;
                item.ReplaceOnComplete = false;
                // Für spätere Sync-Prüfungen merken, welche Fassung geladen wurde.
                if (download is Download file)
                {
                    item.ETag = file.ETag;
                    item.LastModified = file.LastModified;
                }
            }
            item.Download = null;
            item.Cancellation?.Dispose();
            item.Cancellation = null;
            item.RunTask = null;
        }

        bool stillListed;
        lock (_lock)
            stillListed = _items.Contains(item);
        if (stillListed)
        {
            Changed(item);
            if (final == DownloadStatus.Completed)
                ItemCompleted?.Invoke(this, item);
            else if (final == DownloadStatus.Error && !requeue)
                ItemFailed?.Invoke(this, item);
        }
        PumpQueue();
        if (requeue && final == DownloadStatus.Error)
            _ = Task.Delay(RetryDelay).ContinueWith(_ => PumpQueue(), TaskScheduler.Default);
    }

    /// <summary>Wartezeit, bevor eine Warteschlange einen fehlgeschlagenen Download erneut versucht.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(10);

    private readonly Dictionary<long, DateTime> _retryAfter = new();

    /// <summary>Typische Antworten abgelaufener Download-Links (signierte Adressen, Sitzungen).</summary>
    public static bool IsExpiredLink(Exception ex) =>
        ex is HttpStatusException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound
            or System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.Unauthorized };

    private void PumpQueue()
    {
        var toStart = new List<DownloadItem>();
        var finished = new List<QueueFinishedEventArgs>();
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var queue in _queues.Where(q => _runs.ContainsKey(q.Id)).ToList())
            {
                int running = _items.Count(i => i.IsActive && _queueStarted.TryGetValue(i.Id, out long q) && q == queue.Id);
                var waiting = _items.Where(i => i.Status == DownloadStatus.Queued && i.QueueId == queue.Id).ToList();
                int free = Math.Max(1, queue.MaxParallel) - running;
                foreach (var item in waiting
                    .Where(i => !_retryAfter.TryGetValue(i.Id, out var after) || after <= now)
                    .OrderBy(i => i.AddedAt).Take(Math.Max(0, free)))
                {
                    _retryAfter.Remove(item.Id);
                    _queueStarted[item.Id] = queue.Id;
                    _runs[queue.Id]++;
                    toStart.Add(item);
                }

                if (running == 0 && waiting.Count == 0 && !_syncing.Contains(queue.Id))
                {
                    finished.Add(new QueueFinishedEventArgs(queue, _runs[queue.Id] > 0));
                    _runs.Remove(queue.Id);
                }
            }
        }

        foreach (var item in toStart)
            Start(item, byQueue: true);
        if (finished.Count > 0)
        {
            QueueStateChanged?.Invoke(this, EventArgs.Empty);
            foreach (var args in finished)
                QueueFinished?.Invoke(this, args);
        }
    }

    private void Changed(DownloadItem item)
    {
        lock (_lock)
        {
            if (!_items.Contains(item))
                return;
        }
        _database.Update(item);
        ItemChanged?.Invoke(this, item);
    }

    private static void DeletePartialFiles(DownloadItem item)
    {
        if (item.Kind == DownloadKind.Media)
        {
            if (item.Status != DownloadStatus.Completed)
                Media.MediaDownload.DeletePartials(item.Directory,
                    Path.GetFileNameWithoutExtension(item.FileName ?? DownloadItem.NameFromUrl(item.Url)));
            return;
        }
        if (item.FilePath is not { } path)
            return;
        foreach (var file in new[] { DownloadStateStore.TempPathOf(path), DownloadStateStore.StatePathOf(path) })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    /// <summary>Alles pausieren und auf das Speichern der Stände warten.</summary>
    public async ValueTask DisposeAsync()
    {
        List<Task> running;
        lock (_lock)
        {
            _runs.Clear();
            running = _items.Where(i => i.RunTask is not null).Select(i => i.RunTask!).ToList();
            foreach (var item in _items)
                item.Cancellation?.Cancel();
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }
}
