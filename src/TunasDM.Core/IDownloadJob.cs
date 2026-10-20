namespace TunasDM.Core;

/// <summary>Gemeinsame Sicht auf Datei-Downloads (eigene Engine) und Video-Downloads (yt-dlp).</summary>
public interface IDownloadJob
{
    /// <summary>Zielpfad bzw. Startbereitschaft steht fest.</summary>
    event EventHandler? Prepared;

    string? FilePath { get; }
    long TotalSize { get; }
    bool SupportsRanges { get; }
    DownloadProgress GetProgress();
    Task RunAsync(CancellationToken cancellationToken = default);
}
