using System.Diagnostics;

using TunasDM.Core;

namespace TunasDM.Core.Media;

/// <summary>
/// Video-Download über yt-dlp (YouTube, eingebettete Videos, HLS/DASH, direkte Dateien).
/// yt-dlp lädt Bild und Ton getrennt, ffmpeg fügt sie zusammen. Pausieren = Prozess beenden;
/// beim nächsten Start setzt yt-dlp die *.part-Dateien fort.
/// </summary>
public sealed class MediaDownload : IDownloadJob
{
    private const string ProgressPrefix = "TUNAS|";
    private const string FilePrefix = "TUNASFILE|";

    private readonly string _url;
    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _formatCode;
    private readonly string? _referrer;
    private readonly string? _userAgent;
    private readonly SpeedLimiter? _limiter;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Downloaded, long Total)> _streams = new();
    private string? _filePath;
    private long _finalSize = -1;
    private bool _running;

    public MediaDownload(string url, string directory, string baseName, string formatCode,
        string? referrer = null, string? userAgent = null, SpeedLimiter? limiter = null)
    {
        _url = url;
        _directory = directory;
        _baseName = FileNameHelper.Sanitize(baseName);
        _formatCode = formatCode;
        _referrer = referrer;
        _userAgent = userAgent;
        _limiter = limiter;
    }

    public event EventHandler? Prepared;

    public string? FilePath => _filePath;
    public long TotalSize => _finalSize >= 0 ? _finalSize : Sum().Total;
    public bool SupportsRanges => true;

    /// <summary>Dateiname ohne Endung; die Endung bestimmt yt-dlp (mp4, m4a, mp3 …).</summary>
    public string BaseName => _baseName;

    public DownloadProgress GetProgress()
    {
        var (downloaded, total) = Sum();
        if (_finalSize >= 0)
            (downloaded, total) = (_finalSize, _finalSize);
        string name = _filePath is not null ? Path.GetFileName(_filePath) : _baseName;
        return new DownloadProgress(name, total > 0 ? total : -1, downloaded, _running ? 1 : 0, []);
    }

    public IReadOnlyList<string> BuildArguments(string? ffmpegFolder)
    {
        var args = new List<string>
        {
            "--newline", "--progress", "--no-playlist", "--no-warnings", "--encoding", "utf-8",
            "--concurrent-fragments", "4",
            "--progress-template", "download:" + ProgressPrefix
                + "%(info.format_id)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s",
            "--print", "after_move:" + FilePrefix + "%(filepath)s",
            "-o", Path.Combine(_directory, _baseName + ".%(ext)s"),
        };
        if (ffmpegFolder is not null)
            args.AddRange(["--ffmpeg-location", ffmpegFolder]);
        if (_limiter is { BytesPerSecond: > 0 } limiter)
            args.AddRange(["--limit-rate", limiter.BytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        YtDlp.AddHeaders(args, _referrer, _userAgent);
        args.AddRange(YtDlp.FormatArguments(_formatCode));
        args.Add(_url);
        return args;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        string exe = MediaTools.YtDlpPath ?? throw new DownloadException(L.T("yt-dlp wurde nicht gefunden. Bitte in den Optionen einrichten."));
        Directory.CreateDirectory(_directory);
        string? ffmpegFolder = MediaTools.FfmpegPath is { } ffmpeg ? Path.GetDirectoryName(ffmpeg) : null;

        using var process = Process.Start(YtDlp.StartInfo(exe, BuildArguments(ffmpegFolder)))
            ?? throw new DownloadException(L.T("yt-dlp konnte nicht gestartet werden."));
        _running = true;
        Prepared?.Invoke(this, EventArgs.Empty);

        using var kill = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });

        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
            HandleLine(line);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        _running = false;

        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new DownloadException(YtDlp.ErrorText(await errors.ConfigureAwait(false)) ?? L.F("yt-dlp ist mit Code {0} beendet.", process.ExitCode));
        if (_filePath is not null && File.Exists(_filePath))
            _finalSize = new FileInfo(_filePath).Length;
    }

    /// <summary>Eine Zeile der yt-dlp-Ausgabe auswerten (öffentlich für Tests).</summary>
    public void HandleLine(string line)
    {
        line = line.Trim();
        if (line.StartsWith(FilePrefix, StringComparison.Ordinal))
        {
            _filePath = line[FilePrefix.Length..];
            return;
        }
        if (!line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return;

        var parts = line[ProgressPrefix.Length..].Split('|');
        if (parts.Length < 4)
            return;
        long downloaded = YtDlp.ParseLong(parts[1]) ?? 0;
        long total = YtDlp.ParseLong(parts[2]) ?? YtDlp.ParseLong(parts[3]) ?? 0;
        lock (_lock)
            _streams[parts[0]] = (downloaded, Math.Max(total, downloaded));
    }

    private (long Downloaded, long Total) Sum()
    {
        lock (_lock)
            return (_streams.Values.Sum(s => s.Downloaded), _streams.Values.Sum(s => s.Total));
    }

    /// <summary>Angefangene Teile von yt-dlp entfernen (Neu laden / Entfernen).</summary>
    public static void DeletePartials(string directory, string baseName)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, FileNameHelper.Sanitize(baseName) + ".*"))
        {
            if (file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
                || file.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".temp.mp4", StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
        }
    }
}
