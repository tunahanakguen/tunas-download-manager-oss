using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using Microsoft.Win32.SafeHandles;

namespace TunasDM.Core;

/// <summary>
/// Ein Download mit mehreren Verbindungen. Die Zieldatei wird vorab in voller Größe angelegt,
/// jede Verbindung schreibt direkt an ihre Stelle. Der Stand wird regelmäßig gesichert,
/// sodass <see cref="Resume"/> nach Abbruch oder Absturz weitermacht.
/// </summary>
public sealed class Download : IDownloadJob
{
    public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TUNAS-DownloadManager/0.1";

    private readonly DownloadOptions _options;
    private readonly HttpClient _client;
    private readonly SpeedLimiter? _limiter;
    private readonly DownloadRequest? _request;
    private readonly bool _resumed;
    private readonly object _checkpointLock = new();

    private DownloadState? _state;
    private SegmentPlanner? _planner;
    private SafeFileHandle? _handle;
    private FileStream? _flushStream;
    private Exception? _lastTransientError;

    private Download(DownloadOptions? options, HttpClient? client, SpeedLimiter? limiter,
        DownloadRequest? request, DownloadState? state)
    {
        _options = options ?? new DownloadOptions();
        _client = client ?? SharedClient;
        _limiter = limiter;
        _request = request;
        _state = state;
        _resumed = state is not null;
    }

    public static Download Create(DownloadRequest request, DownloadOptions? options = null,
        HttpClient? client = null, SpeedLimiter? limiter = null) =>
        new(options, client, limiter, request, null);

    public static Download Resume(string statePath, DownloadOptions? options = null,
        HttpClient? client = null, SpeedLimiter? limiter = null) =>
        new(options, client, limiter, null, DownloadStateStore.Load(statePath));

    /// <summary>Endgültiger Pfad; erst nach dem Start bekannt.</summary>
    public string? FilePath => _state is { FilePath.Length: > 0 } s ? s.FilePath : null;

    public long TotalSize => _state?.TotalSize ?? -1;

    public bool SupportsRanges => _state?.SupportsRanges ?? false;

    /// <summary>Kennungen der Datei auf dem Server (für spätere Sync-Prüfungen).</summary>
    public string? ETag => _state?.ETag;

    public DateTimeOffset? LastModified => _state?.LastModified;

    /// <summary>
    /// Sync-Prüfung: hat sich die Datei auf dem Server geändert? Fragt mit <c>If-None-Match</c> bzw.
    /// <c>If-Modified-Since</c> nur das erste Byte ab (304 = unverändert) und vergleicht sonst ETag, Datum und Größe.
    /// Ohne gespeicherte Kennung gilt nur eine andere Größe als Änderung.
    /// </summary>
    public static async Task<ChangeCheck> CheckForChangeAsync(DownloadRequest request, string? etag, DateTimeOffset? lastModified,
        long size, HttpClient? client = null, CancellationToken cancellationToken = default)
    {
        client ??= SharedClient;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if (IsFtp(request.Url))
        {
            // FTP kennt kein ETag: Größe und Änderungsdatum vergleichen.
            await using var connection = await Ftp.FtpConnection.OpenAsync(request.Url, timeout.Token).ConfigureAwait(false);
            string path = Ftp.FtpConnection.PathOf(request.Url);
            long ftpSize = await connection.SizeAsync(path, timeout.Token).ConfigureAwait(false);
            var ftpModified = await connection.ModifiedAsync(path, timeout.Token).ConfigureAwait(false);
            bool ftpChanged = (ftpSize >= 0 && size >= 0 && ftpSize != size)
                || (lastModified is { } was && ftpModified is { } now && now > was);
            return new ChangeCheck(ftpChanged, null, ftpModified ?? lastModified, ftpSize >= 0 ? ftpSize : size);
        }
        bool strongTag = etag is not null && !etag.StartsWith("W/", StringComparison.Ordinal)
            && EntityTagHeaderValue.TryParse(etag, out _);
        using var response = await SendFollowingRedirectsAsync(client, request.Url, (url, sameSite) =>
        {
            var message = BuildRequest(url, request.UserAgent, request.Referrer, request.Cookies, request.Headers, sameSite);
            message.Headers.Range = new RangeHeaderValue(0, 0);
            if (strongTag)
                message.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag!));
            else if (lastModified is { } modified)
                message.Headers.IfModifiedSince = modified;
            return message;
        }, timeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
            return new ChangeCheck(false, etag, lastModified, size);
        if (!response.IsSuccessStatusCode)
            throw new HttpStatusException(response.StatusCode);

        string? newTag = response.Headers.ETag?.ToString();
        DateTimeOffset? newModified = response.Content.Headers.LastModified;
        long newSize = response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is long length
            ? length
            : response.Content.Headers.ContentLength ?? -1;

        bool changed = (newSize >= 0 && size >= 0 && newSize != size)
            || (etag is not null && newTag is not null && newTag != etag)
            || (etag is null && lastModified is { } before && newModified is { } after && after > before);
        return new ChangeCheck(changed, newTag ?? etag, newModified ?? lastModified, newSize >= 0 ? newSize : size);
    }

    /// <summary>Zielpfad, Größe und Range-Unterstützung stehen fest; die Teil-Datei ist angelegt.</summary>
    public event EventHandler? Prepared;

    /// <summary>
    /// Fragt Name, Größe und Range-Unterstützung ab, ohne zu laden (für den Datei-Info-Dialog).
    /// Nutzt <c>Range: bytes=0-0</c>, weil viele Server HEAD falsch beantworten.
    /// </summary>
    public static async Task<RemoteFileInfo> ProbeAsync(DownloadRequest request, HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        client ??= SharedClient;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        if (IsFtp(request.Url))
            return await ProbeFtpInfoAsync(request, timeout.Token).ConfigureAwait(false);
        using var response = await SendFollowingRedirectsAsync(client, request.Url, (url, sameSite) =>
        {
            var message = BuildRequest(url, request.UserAgent, request.Referrer, request.Cookies, request.Headers, sameSite);
            message.Headers.Range = new RangeHeaderValue(0, 0);
            return message;
        }, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpStatusException(response.StatusCode);

        bool ranges = response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.Length is > 0;
        long size = ranges ? response.Content.Headers.ContentRange!.Length!.Value : response.Content.Headers.ContentLength ?? -1;
        string name = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameHelper.FromResponse(response, request.Url)
            : FileNameHelper.Sanitize(request.FileName);
        return new RemoteFileInfo(response.RequestMessage?.RequestUri ?? request.Url, name, size, ranges,
            response.Content.Headers.ContentType?.MediaType);
    }

    private static HttpClient _sharedClient = CreateHttpClient();

    /// <summary>Client für alle Downloads ohne eigenen Client; wird bei geänderten Proxy-Einstellungen ersetzt.</summary>
    private static HttpClient SharedClient => Volatile.Read(ref _sharedClient);

    /// <summary>
    /// Proxy für neue Verbindungen setzen. Laufende Verbindungen behalten den alten Client bis zu ihrem Ende,
    /// deshalb wird er nicht verworfen.
    /// </summary>
    public static void UseProxy(ProxySettings? proxy) => Volatile.Write(ref _sharedClient, CreateHttpClient(proxy));

    /// <summary>So oft wurde eine zu langsame Verbindung abgebrochen und neu aufgebaut.</summary>
    public int SlowConnectionsDropped => Volatile.Read(ref _slowConnectionsDropped);

    private int _slowConnectionsDropped;

    /// <summary>
    /// Gemeinsamer Client: HTTP/1.1 (echte Parallelverbindungen), keine Kompression wegen Ranges.
    /// Weiterleitungen folgt <see cref="SendFollowingRedirectsAsync"/> selbst, damit Cookies nicht an fremde Hosts gehen.
    /// </summary>
    public static HttpClient CreateHttpClient(ProxySettings? proxy = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
        };
        proxy?.ApplyTo(handler);
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public const int MaxRedirects = 10;

    /// <summary>
    /// Sendet und folgt Weiterleitungen. <paramref name="build"/> bekommt die Adresse und ob es noch dieselbe Site ist:
    /// Cookies und Anmeldedaten gehen nur an den Host der ursprünglichen Adresse (oder seine Subdomains)
    /// und nie von https auf http.
    /// </summary>
    internal static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(HttpClient client, Uri url,
        Func<Uri, bool, HttpRequestMessage> build, CancellationToken token)
    {
        Uri current = url;
        for (int hop = 0; ; hop++)
        {
            var response = await client.SendAsync(build(current, IsSameSite(url, current)), HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose();
                if (hop >= MaxRedirects)
                    throw new DownloadException(L.T("Zu viele Weiterleitungen."));
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (current.Scheme is not ("http" or "https"))
                    throw new DownloadException(L.F("Weiterleitung auf eine nicht unterstützte Adresse: {0}:", current.Scheme));
                continue;
            }
            return response;
        }
    }

    internal static bool IsSameSite(Uri origin, Uri target)
    {
        if (origin.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps)
            return false;
        string host = origin.IdnHost;
        return string.Equals(target.IdnHost, host, StringComparison.OrdinalIgnoreCase)
            || target.IdnHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    }

    public DownloadProgress GetProgress()
    {
        var state = _state;
        string? name = state is { FilePath.Length: > 0 } ? Path.GetFileName(state.FilePath) : null;
        if (_planner is not { } planner)
            return new DownloadProgress(name, state?.TotalSize ?? -1, 0, 0, []);

        var (segments, downloaded, active) = planner.View();
        return new DownloadProgress(name, state!.TotalSize, downloaded, active, segments);
    }

    /// <summary>Lädt bis zum Ende. Bei Abbruch wird der Stand gesichert und eine OperationCanceledException geworfen.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        OpenedRange? firstResponse = _state is null ? await ProbeAsync(cts.Token).ConfigureAwait(false) : null;

        OpenFile();
        var planner = _planner!;
        int workerCount = planner.CanSplit ? Math.Max(1, _options.MaxConnections) : 1;

        // Die erste Verbindung liest die Probe-Antwort direkt weiter; die übrigen teilen sich ein.
        var workers = new Task[workerCount];
        workers[0] = WorkerLoopAsync(0, planner.Acquire(0), firstResponse, cts);
        for (int i = 1; i < workerCount; i++)
            workers[i] = WorkerLoopAsync(i, null, null, cts);

        using var helpersStop = new CancellationTokenSource();
        var saver = SaveLoopAsync(helpersStop.Token);
        var monitor = MonitorSlowConnectionsAsync(helpersStop.Token);

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            // Ausgewertet wird unten über die einzelnen Tasks.
        }

        helpersStop.Cancel();
        await Task.WhenAll(saver, monitor).ConfigureAwait(false);

        if (planner.IsComplete)
        {
            Complete();
            return;
        }

        Exception? fatal = workers.Where(t => t.IsFaulted).Select(t => t.Exception!.InnerException!).FirstOrDefault();
        Pause();

        if (fatal is not null)
            ExceptionDispatchInfo.Throw(fatal);
        cancellationToken.ThrowIfCancellationRequested();
        throw new DownloadException(L.T("Download konnte nicht abgeschlossen werden."), _lastTransientError);
    }

    private async Task<OpenedRange> ProbeAsync(CancellationToken token)
    {
        var request = _request!;
        var state = new DownloadState
        {
            Url = request.Url.ToString(),
            Referrer = request.Referrer,
            Cookies = request.Cookies,
            UserAgent = request.UserAgent,
            Headers = new Dictionary<string, string>(request.Headers),
            ReplaceExisting = request.ReplaceExisting,
        };
        _state = state;
        if (IsFtp(request.Url))
            return await ProbeFtpAsync(state, token).ConfigureAwait(false);

        HttpResponseMessage response;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                response = await SendAsync(0, null, conditional: false, timeout, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var error = new HttpStatusException(response.StatusCode);
                    response.Dispose();
                    throw error;
                }
                break;
            }
            catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested && attempt <= _options.MaxRetries)
            {
                _lastTransientError = ex;
                await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
        }

        if (response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange is { From: 0, Length: long length })
        {
            state.TotalSize = length;
            state.SupportsRanges = length > 0;
        }
        else
        {
            state.TotalSize = response.StatusCode == HttpStatusCode.OK ? response.Content.Headers.ContentLength ?? -1 : -1;
            state.SupportsRanges = false;
        }
        state.ETag = response.Headers.ETag?.ToString();
        state.LastModified = response.Content.Headers.LastModified;

        string name = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameHelper.FromResponse(response, request.Url)
            : FileNameHelper.Sanitize(request.FileName);
        SetTarget(state, request, name);
        try
        {
            return new OpenedRange(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void SetTarget(DownloadState state, DownloadRequest request, string name)
    {
        string directory = Path.GetFullPath(request.TargetDirectory);
        Directory.CreateDirectory(directory);
        // Beim Ersetzen bleibt der Name; die alte Datei bleibt bis zum Ende unberührt (Teil-Datei: name.tunasdm).
        state.FilePath = Path.Combine(directory, request.ReplaceExisting ? name : FileNameHelper.MakeUnique(directory, name));
        state.Segments = [new SegmentState { Start = 0, Position = 0, End = state.TotalSize >= 0 ? state.TotalSize : long.MaxValue }];
    }

    // ---------- FTP ----------

    internal static bool IsFtp(Uri url) => DownloadSchemes.IsFtp(url);

    /// <summary>
    /// FTP: Größe (SIZE), Datum (MDTM) und ob der Server ab einer Stelle weiterladen kann (REST). Mehrere Verbindungen
    /// laden dann je ein Segment mit REST; jede beendet ihre Verbindung, sobald ihr Segment voll ist.
    /// </summary>
    private async Task<OpenedRange> ProbeFtpAsync(DownloadState state, CancellationToken token)
    {
        var request = _request!;
        for (int attempt = 1; ; attempt++)
        {
            Ftp.FtpConnection? connection = null;
            try
            {
                connection = await Ftp.FtpConnection.OpenAsync(request.Url, token).ConfigureAwait(false);
                string path = Ftp.FtpConnection.PathOf(request.Url);
                state.TotalSize = await connection.SizeAsync(path, token).ConfigureAwait(false);
                state.LastModified = await connection.ModifiedAsync(path, token).ConfigureAwait(false);
                state.SupportsRanges = state.TotalSize > 0 && await connection.SupportsRestartAsync(token).ConfigureAwait(false);
                string name = string.IsNullOrWhiteSpace(request.FileName)
                    ? FileNameHelper.Sanitize(Path.GetFileName(path) is { Length: > 0 } file ? file : request.Url.Host)
                    : FileNameHelper.Sanitize(request.FileName);
                SetTarget(state, request, name);
                // Die erste Verbindung lädt gleich ab dem Anfang weiter.
                var stream = await connection.RetrieveAsync(path, 0, token).ConfigureAwait(false);
                return new OpenedRange(stream, null);
            }
            catch (Exception ex) when (IsTransient(ex) && !token.IsCancellationRequested && attempt <= _options.MaxRetries)
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
                _lastTransientError = ex;
                await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
            }
            catch
            {
                if (connection is not null)
                    await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Neue FTP-Verbindung für ein Segment; prüft vorher, ob die Datei noch dieselbe ist.</summary>
    private async Task<OpenedRange> OpenFtpRangeAsync(long from, CancellationToken token)
    {
        var state = _state!;
        var url = new Uri(state.Url);
        string path = Ftp.FtpConnection.PathOf(url);
        var connection = await Ftp.FtpConnection.OpenAsync(url, token).ConfigureAwait(false);
        try
        {
            if (state.SupportsRanges)
            {
                long size = await connection.SizeAsync(path, token).ConfigureAwait(false);
                if (size >= 0 && size != state.TotalSize)
                    throw new ServerFileChangedException(L.F("Die Dateigröße auf dem Server hat sich geändert ({0} → {1} Bytes).", state.TotalSize, size));
                if (!_options.IgnoreModificationTimeChanges && state.LastModified is { } known
                    && await connection.ModifiedAsync(path, token).ConfigureAwait(false) is { } modified && modified != known)
                    throw new ServerFileChangedException(L.T("Die Datei auf dem Server wurde geändert."));
            }
            var stream = await connection.RetrieveAsync(path, state.SupportsRanges ? from : 0, token).ConfigureAwait(false);
            return new OpenedRange(stream, null);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Datei-Info-Dialog für FTP: Name, Größe, Fortsetzen möglich.</summary>
    private static async Task<RemoteFileInfo> ProbeFtpInfoAsync(DownloadRequest request, CancellationToken token)
    {
        await using var connection = await Ftp.FtpConnection.OpenAsync(request.Url, token).ConfigureAwait(false);
        string path = Ftp.FtpConnection.PathOf(request.Url);
        long size = await connection.SizeAsync(path, token).ConfigureAwait(false);
        bool restart = size > 0 && await connection.SupportsRestartAsync(token).ConfigureAwait(false);
        string name = string.IsNullOrWhiteSpace(request.FileName)
            ? FileNameHelper.Sanitize(Path.GetFileName(path) is { Length: > 0 } file ? file : request.Url.Host)
            : FileNameHelper.Sanitize(request.FileName);
        return new RemoteFileInfo(request.Url, name, size, restart, null);
    }

    private void OpenFile()
    {
        var state = _state!;
        string tempPath = DownloadStateStore.TempPathOf(state.FilePath);
        bool resumed = _resumed;

        if (resumed && !File.Exists(tempPath))
        {
            // Teil-Datei fehlt: von vorne.
            state.Segments = [new SegmentState { Start = 0, Position = 0, End = state.TotalSize }];
            resumed = false;
        }

        if (!resumed)
            StorageCheck.EnsureFits(Path.GetDirectoryName(tempPath)!, state.TotalSize);
        _handle = File.OpenHandle(tempPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        if (!resumed)
            RandomAccess.SetLength(_handle, Math.Max(0, state.TotalSize));
        _flushStream = new FileStream(_handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);

        _planner = new SegmentPlanner(state.Segments, _options.EffectiveMinSegmentSize,
            canSplit: state.SupportsRanges && state.TotalSize > 0, resumed);
        SaveCheckpoint();
        Prepared?.Invoke(this, EventArgs.Empty);
    }

    private async Task WorkerLoopAsync(int id, Segment? segment, OpenedRange? preOpened, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var planner = _planner!;
        int failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                segment ??= planner.Acquire(id);
                if (segment is null)
                    return;

                var current = segment;
                var response = preOpened;
                preOpened = null;
                try
                {
                    await DownloadSegmentAsync(current, response, token).ConfigureAwait(false);
                    failures = 0;
                    planner.Release(current);
                    segment = null;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception) when (current.AbortRequested)
                {
                    // Zu langsam: Segment freigeben und sofort neu verbinden (kein Fehlversuch).
                    Interlocked.Increment(ref _slowConnectionsDropped);
                    planner.Release(current);
                    segment = null;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    _lastTransientError = ex;
                    if (!_state!.SupportsRanges)
                        planner.Reset(current);
                    planner.Release(current);
                    segment = null;

                    // Diese Verbindung gibt auf; ihr Rest wird von den anderen übernommen.
                    if (++failures > _options.MaxRetries)
                        return;
                    try
                    {
                        await Task.Delay(Backoff(failures), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
                catch
                {
                    cts.Cancel();
                    throw;
                }
            }
        }
        finally
        {
            if (preOpened is not null)
                await preOpened.DisposeAsync().ConfigureAwait(false);
            if (segment is not null)
                planner.Release(segment);
        }
    }

    private async Task DownloadSegmentAsync(Segment segment, OpenedRange? preOpened, CancellationToken token)
    {
        int verify = segment.NeedsVerify ? (int)Math.Min(_options.VerifyBytes, segment.Position - segment.Start) : 0;
        long from = segment.Position - verify;

        // Abbruch durch die Überwachung läuft über ein eigenes Token pro Segment.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(token);
        segment.Abort = abort;
        try
        {
            await DownloadSegmentCoreAsync(segment, preOpened, from, verify, abort.Token).ConfigureAwait(false);
        }
        finally
        {
            segment.Abort = null;
        }
    }

    private async Task DownloadSegmentCoreAsync(Segment segment, OpenedRange? preOpened, long from, int verify,
        CancellationToken token)
    {
        var state = _state!;
        var planner = _planner!;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var opened = preOpened ?? await OpenRangeAsync(from, segment.End, timeout, token).ConfigureAwait(false);

        await using (opened)
        {
            timeout.CancelAfter(_options.ReadTimeout);
            var stream = opened.Stream;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(_options.BufferSize, _options.VerifyBytes));
            try
            {
                if (verify > 0)
                {
                    await VerifyAsync(stream, from, verify, buffer, timeout, token).ConfigureAwait(false);
                    segment.NeedsVerify = false;
                }

                while (true)
                {
                    int wanted = planner.Reserve(segment, _options.BufferSize);
                    if (wanted == 0)
                        break;

                    int read = await ReadAsync(stream, buffer.AsMemory(0, wanted), timeout, token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        planner.Commit(segment, 0);
                        if (state.TotalSize < 0)
                        {
                            planner.FinishAt(segment);
                            break;
                        }
                        throw new IOException(L.T("Der Server hat die Verbindung vorzeitig beendet."));
                    }

                    if (_limiter is not null)
                        await _limiter.WaitAsync(read, token).ConfigureAwait(false);

                    try
                    {
                        await RandomAccess.WriteAsync(_handle!, buffer.AsMemory(0, read), segment.Position, token).ConfigureAwait(false);
                    }
                    catch (IOException ex)
                    {
                        throw new DownloadException(L.F("Schreibfehler: {0}", ex.Message), ex);
                    }
                    planner.Commit(segment, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>Die letzten vorhandenen Bytes neu laden und vergleichen: passt der Anschluss noch?</summary>
    private async Task VerifyAsync(Stream stream, long offset, int count, byte[] buffer,
        CancellationTokenSource timeout, CancellationToken token)
    {
        int filled = 0;
        while (filled < count)
        {
            int read = await ReadAsync(stream, buffer.AsMemory(filled, count - filled), timeout, token).ConfigureAwait(false);
            if (read == 0)
                throw new IOException(L.T("Der Server hat die Verbindung vorzeitig beendet."));
            filled += read;
        }

        byte[] local = new byte[count];
        int localRead = await RandomAccess.ReadAsync(_handle!, local, offset, token).ConfigureAwait(false);
        if (localRead != count || !local.AsSpan().SequenceEqual(buffer.AsSpan(0, count)))
            throw new ServerFileChangedException(L.T("Die Datei auf dem Server passt nicht mehr zu den bereits geladenen Teilen."));
    }

    private async Task<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationTokenSource timeout, CancellationToken token)
    {
        timeout.CancelAfter(_options.ReadTimeout);
        try
        {
            return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("Keine Daten mehr vom Server (Zeitüberschreitung)."));
        }
    }

    /// <summary>Verbindung für ein Segment ab <paramref name="from"/> öffnen und prüfen (HTTP-Range bzw. FTP-REST).</summary>
    private async Task<OpenedRange> OpenRangeAsync(long from, long end, CancellationTokenSource timeout, CancellationToken token)
    {
        var state = _state!;
        if (IsFtp(new Uri(state.Url)))
        {
            timeout.CancelAfter(_options.ReadTimeout);
            try
            {
                return await OpenFtpRangeAsync(from, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException(L.T("Zeitüberschreitung beim Verbinden."));
            }
        }

        var response = await SendAsync(
            state.SupportsRanges ? from : null,
            state.SupportsRanges ? end - 1 : null,
            conditional: true, timeout, token).ConfigureAwait(false);
        try
        {
            ValidateResponse(response, from);
            timeout.CancelAfter(_options.ReadTimeout);
            return new OpenedRange(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(long? from, long? to, bool conditional,
        CancellationTokenSource timeout, CancellationToken token)
    {
        var state = _state!;
        HttpRequestMessage Build(Uri url, bool sameSite)
        {
            var request = BuildRequest(url, state.UserAgent, state.Referrer, state.Cookies, state.Headers, sameSite);
            if (from is long start)
            {
                request.Headers.Range = new RangeHeaderValue(start, to);
                if (conditional && IfRangeCondition() is { } condition)
                    request.Headers.IfRange = condition;
            }
            return request;
        }

        timeout.CancelAfter(_options.ReadTimeout);
        try
        {
            return await SendFollowingRedirectsAsync(_client, new Uri(state.Url), Build, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(L.T("Zeitüberschreitung beim Verbinden."));
        }
    }

    /// <summary>Anfrage bauen; an fremde Hosts (<paramref name="sameSite"/> = false) ohne Cookies und Anmeldedaten.</summary>
    internal static HttpRequestMessage BuildRequest(Uri url, string? userAgent, string? referrer, string? cookies,
        IReadOnlyDictionary<string, string> headers, bool sameSite)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? DefaultUserAgent);
        request.Headers.AcceptEncoding.ParseAdd("identity");
        if (Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri))
            request.Headers.Referrer = referrerUri;
        if (sameSite && !string.IsNullOrEmpty(cookies))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        foreach (var (name, value) in headers)
        {
            if (!sameSite && (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)))
                continue;
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }

    private RangeConditionHeaderValue? IfRangeCondition()
    {
        var state = _state!;
        if (state.ETag is { } etag && !etag.StartsWith("W/", StringComparison.Ordinal)
            && EntityTagHeaderValue.TryParse(etag, out var tag))
            return new RangeConditionHeaderValue(tag);
        if (!_options.IgnoreModificationTimeChanges && state.LastModified is { } lastModified)
            return new RangeConditionHeaderValue(lastModified);
        return null;
    }

    private void ValidateResponse(HttpResponseMessage response, long from)
    {
        var state = _state!;
        if (!response.IsSuccessStatusCode)
            throw new HttpStatusException(response.StatusCode);
        if (!state.SupportsRanges)
            return;

        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new ServerFileChangedException(L.T("Der Server liefert die Datei nicht mehr in Teilen; sie wurde vermutlich geändert."));

        var range = response.Content.Headers.ContentRange;
        if (range?.From != from)
            throw new DownloadException(L.F("Der Server hat einen falschen Bereich geliefert ({0}).", range));
        if (range.Length is long length && length != state.TotalSize)
            throw new ServerFileChangedException(L.F("Die Dateigröße auf dem Server hat sich geändert ({0} → {1} Bytes).", state.TotalSize, length));
    }

    /// <summary>
    /// Vergleicht jede Verbindung mit dem Durchschnitt der übrigen. Bleibt eine über die Karenzzeit deutlich
    /// darunter, wird sie abgebrochen; ihr Segment wird neu vergeben. Mit Geschwindigkeitslimit aus,
    /// weil der Begrenzer die Verbindungen ungleich bremst.
    /// </summary>
    private async Task MonitorSlowConnectionsAsync(CancellationToken token)
    {
        if (!_options.DropSlowConnections || !_planner!.CanSplit || _options.MaxConnections < 2)
            return;

        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.SlowConnectionGrace.TotalMilliseconds / 4, 100, 1000));
        int ticksNeeded = (int)Math.Ceiling(_options.SlowConnectionGrace / interval);
        var samples = new Dictionary<Segment, (long Bytes, int SlowTicks)>();
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_limiter is { BytesPerSecond: > 0 })
                {
                    samples.Clear();
                    continue;
                }

                var active = _planner.ActiveSegments();
                var speeds = new Dictionary<Segment, long>();
                foreach (var segment in active)
                {
                    long bytes = _planner.TransferredOf(segment);
                    bool oldEnough = System.Diagnostics.Stopwatch.GetElapsedTime(segment.AcquiredAt) >= _options.SlowConnectionGrace;
                    if (samples.TryGetValue(segment, out var previous) && oldEnough)
                        speeds[segment] = bytes - previous.Bytes;
                    samples[segment] = (bytes, previous.SlowTicks);
                }
                foreach (var gone in samples.Keys.Except(active).ToList())
                    samples.Remove(gone);

                if (speeds.Count < 2)
                    continue;

                long total = speeds.Values.Sum();
                foreach (var (segment, speed) in speeds)
                {
                    double othersAverage = (total - speed) / (double)(speeds.Count - 1);
                    bool slow = othersAverage > 0 && speed < othersAverage * _options.SlowConnectionRatio
                        && _planner.RemainingOf(segment) >= 2 * _options.EffectiveMinSegmentSize;
                    int slowTicks = slow ? samples[segment].SlowTicks + 1 : 0;
                    samples[segment] = (samples[segment].Bytes, slowTicks);

                    if (slowTicks >= ticksNeeded && segment.Abort is { } abort)
                    {
                        segment.AbortRequested = true;
                        abort.Cancel();
                        samples.Remove(segment);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SaveLoopAsync(CancellationToken token)
    {
        if (!_state!.SupportsRanges)
            return;

        using var timer = new PeriodicTimer(_options.SaveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                SaveCheckpoint();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Erst Positionen merken, dann Daten auf die Platte, dann Stand schreiben.</summary>
    private void SaveCheckpoint()
    {
        var state = _state!;
        if (!state.SupportsRanges || _planner is null || _flushStream is null)
            return;

        lock (_checkpointLock)
        {
            var snapshot = _planner.Snapshot();
            _flushStream.Flush(flushToDisk: true);
            state.Segments = snapshot;
            DownloadStateStore.Save(DownloadStateStore.StatePathOf(state.FilePath), state);
        }
    }

    private void Complete()
    {
        var state = _state!;
        _flushStream!.Flush(flushToDisk: true);
        if (state.TotalSize < 0)
            state.TotalSize = RandomAccess.GetLength(_handle!);
        CloseFile();

        string directory = Path.GetDirectoryName(state.FilePath)!;
        string target = state.FilePath;
        if (File.Exists(target) && !state.ReplaceExisting)
            target = Path.Combine(directory, FileNameHelper.MakeUnique(directory, Path.GetFileName(target)));

        File.Move(DownloadStateStore.TempPathOf(state.FilePath), target, overwrite: state.ReplaceExisting);
        File.Delete(DownloadStateStore.StatePathOf(state.FilePath));
        if (state.LastModified is { } lastModified)
            File.SetLastWriteTimeUtc(target, lastModified.UtcDateTime);
        state.FilePath = target;
    }

    private void Pause()
    {
        var state = _state!;
        if (state.SupportsRanges)
        {
            SaveCheckpoint();
            CloseFile();
        }
        else
        {
            // Ohne Range-Unterstützung lässt sich nicht fortsetzen.
            CloseFile();
            File.Delete(DownloadStateStore.TempPathOf(state.FilePath));
        }
    }

    private void CloseFile()
    {
        _flushStream?.Dispose();
        _flushStream = null;
        _handle?.Dispose();
        _handle = null;
    }

    private TimeSpan Backoff(int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(30_000, _options.RetryDelay.TotalMilliseconds * Math.Pow(2, failures - 1)));

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpStatusException status => status.IsTransient,
        Ftp.FtpException ftp => ftp.IsTransient, // z. B. 421 „zu viele Verbindungen“
        DownloadException => false,
        HttpRequestException or IOException or TimeoutException or System.Net.Sockets.SocketException => true,
        OperationCanceledException => true,
        _ => false,
    };
}

/// <summary>Geöffneter Datenstrom einer Verbindung samt dem, was beim Schließen mit weg muss (HTTP-Antwort).</summary>
internal sealed class OpenedRange(Stream stream, IDisposable? owner) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            // Vorzeitig geschlossene Verbindung (Segment fertig oder abgebrochen).
        }
        owner?.Dispose();
    }
}
