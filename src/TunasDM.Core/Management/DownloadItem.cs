namespace TunasDM.Core.Management;

public enum DownloadKind
{
    /// <summary>Datei über die eigene Engine.</summary>
    File = 0,

    /// <summary>Video/Audio über yt-dlp (YouTube, Streams).</summary>
    Media = 1,
}

public enum DownloadStatus
{
    /// <summary>Wartet in der Warteschlange.</summary>
    Queued = 0,
    Connecting = 1,
    Downloading = 2,
    Paused = 3,
    Completed = 4,
    Error = 5,
}

/// <summary>Ein Eintrag der Download-Liste (gespeichert in SQLite).</summary>
public sealed class DownloadItem
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string? FileName { get; set; }
    public string Directory { get; set; } = "";
    public string? FilePath { get; set; }
    public string Category { get; set; } = Management.Category.General;
    public DownloadStatus Status { get; set; }
    public long TotalSize { get; set; } = -1;
    public long Downloaded { get; set; }
    public bool SupportsRanges { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public string? Description { get; set; }
    public DownloadKind Kind { get; set; }

    /// <summary>Gewählte Qualität bei Videos, z. B. „v720“ oder „a-mp3“ (siehe YtDlp).</summary>
    public string? MediaFormat { get; set; }

    /// <summary>Warteschlange, in der der Download wartet bzw. zuletzt gewartet hat.</summary>
    public long QueueId { get; set; } = DownloadQueue.MainId;

    /// <summary>Kennung der fertigen Datei auf dem Server (für Sync-Warteschlangen).</summary>
    public string? ETag { get; set; }

    public DateTimeOffset? LastModified { get; set; }

    /// <summary>Sync hat eine neuere Fassung gefunden: die vorhandene Datei erst am Ende ersetzen (kein „(2)“).</summary>
    public bool ReplaceOnComplete { get; set; }

    /// <summary>Zuletzt von einer Warteschlange gestartet (nicht von Hand); dann kein Fenster je fertiger Datei.</summary>
    public bool StartedByQueue { get; internal set; }

    internal IDownloadJob? Download { get; set; }
    internal CancellationTokenSource? Cancellation { get; set; }
    internal Task? RunTask { get; set; }

    public bool IsActive => Status is DownloadStatus.Connecting or DownloadStatus.Downloading;

    public bool CanStart => Status is DownloadStatus.Queued or DownloadStatus.Paused or DownloadStatus.Error;

    public string DisplayName =>
        FileName ?? (FilePath is not null ? Path.GetFileName(FilePath) : NameFromUrl(Url));

    /// <summary>Aktueller Stand; während des Ladens direkt aus der Engine.</summary>
    public DownloadProgress Progress =>
        Download?.GetProgress() is { } live && (live.TotalSize >= 0 || live.Downloaded > 0)
            ? live with { FileName = live.FileName ?? DisplayName }
            : new DownloadProgress(DisplayName, TotalSize, Downloaded, 0, []);

    public static string NameFromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "download";
        string name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        return string.IsNullOrWhiteSpace(name) ? uri.Host : FileNameHelper.Sanitize(name);
    }
}
