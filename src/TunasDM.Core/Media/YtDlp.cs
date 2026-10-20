using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using TunasDM.Core;

namespace TunasDM.Core.Media;

/// <summary>Ein Video mit seinen wählbaren Qualitäten (aus <c>yt-dlp -J</c>).</summary>
public sealed record MediaInfo(
    string Title,
    double? Duration,
    string? Thumbnail,
    string? Site,
    string WebpageUrl,
    IReadOnlyList<MediaOption> Options);

/// <summary>Eine Wahlmöglichkeit, z. B. „720p“ oder „Nur Audio · MP3“. <see cref="Code"/> wird gespeichert.</summary>
public sealed record MediaOption(string Code, string Label, string Detail, long? EstimatedSize, bool IsAudio);

/// <summary>Anbindung an yt-dlp: Formate abfragen und Kommandozeilen bauen.</summary>
public static class YtDlp
{
    public const string BestCode = "best";
    public const string AudioM4aCode = "a-m4a";
    public const string AudioMp3Code = "a-mp3";

    public static async Task<MediaInfo> ProbeAsync(string url, string? referrer = null, string? userAgent = null,
        CancellationToken cancellationToken = default)
    {
        string exe = MediaTools.YtDlpPath ?? throw new DownloadException(L.T("yt-dlp wurde nicht gefunden."));
        var args = new List<string> { "-J", "--no-playlist", "--no-warnings", "--encoding", "utf-8" };
        AddHeaders(args, referrer, userAgent);
        args.Add(url);

        var (exitCode, output, error) = await RunAsync(exe, args, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0 || output.Length == 0)
            throw new DownloadException(ErrorText(error) ?? L.T("Für diese Seite wurde kein Video gefunden."));
        return Parse(output);
    }

    /// <summary>JSON von <c>yt-dlp -J</c> in Titel und Qualitäten übersetzen.</summary>
    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double? duration = Number(root, "duration");
        var formats = root.TryGetProperty("formats", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(Format.From).ToList()
            : [];
        // Eine Liste mehrerer Videos (Playlist, Seite mit mehreren Playern) ist keine Auswahl für genau ein Video.
        bool isList = root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array;

        return new MediaInfo(
            Text(root, "title") ?? "Video",
            duration,
            Text(root, "thumbnail"),
            Text(root, "extractor_key") ?? Text(root, "extractor"),
            Text(root, "webpage_url") ?? Text(root, "original_url") ?? "",
            isList && formats.Count == 0 ? [] : BuildOptions(formats, duration));
    }

    private static List<MediaOption> BuildOptions(List<Format> formats, double? duration)
    {
        var options = new List<MediaOption>();
        var audio = formats.Where(f => f.IsAudioOnly).ToList();
        var bestM4a = audio.Where(f => f.Ext == "m4a").OrderByDescending(f => f.Bitrate).FirstOrDefault()
            ?? audio.OrderByDescending(f => f.Bitrate).FirstOrDefault();

        foreach (int height in formats.Where(f => f.HasVideo && f.Height > 0).Select(f => f.Height).Distinct().OrderDescending())
        {
            // Für die Größe dasselbe wählen wie später yt-dlp: H.264 bevorzugt, sonst das beste andere.
            var candidates = formats.Where(f => f.HasVideo && f.Height == height).ToList();
            var video = candidates.Where(f => f.IsH264).OrderByDescending(f => f.Bitrate).FirstOrDefault()
                ?? candidates.OrderByDescending(f => f.Bitrate).First();
            long? size = video.Size(duration) is long v
                ? v + (video.HasAudio ? 0 : bestM4a?.Size(duration) ?? 0)
                : null;
            string label = height switch { >= 2160 => $"{height}p · 4K", >= 1440 => $"{height}p · 2K", >= 720 => $"{height}p · HD", _ => $"{height}p" };
            string codec = video.IsH264 ? "H.264" : video.Codec;
            options.Add(new MediaOption($"v{height}", label, $"MP4 · {codec}", size, IsAudio: false));
        }

        if (options.Count == 0 && formats.Count <= 1)
            options.Add(new MediaOption(BestCode, L.T("Beste Qualität"), L.T("Datei wie angeboten"), formats.FirstOrDefault()?.Size(duration), IsAudio: false));

        if (bestM4a is not null || formats.Any(f => f.HasAudio))
        {
            options.Add(new MediaOption(AudioM4aCode, L.T("Nur Audio · M4A"), L.T("Originalton, ohne Umwandlung"), bestM4a?.Size(duration), IsAudio: true));
            long? mp3 = duration is double d ? (long)(d * 192_000 / 8) : null;
            options.Add(new MediaOption(AudioMp3Code, L.T("Nur Audio · MP3"), L.T("192 kbit/s, für alle Player"), mp3, IsAudio: true));
        }
        return options;
    }

    /// <summary>yt-dlp-Argumente zur Auswahl. Bevorzugt H.264 + AAC, damit jedes Abspielprogramm die Datei öffnet.</summary>
    public static IReadOnlyList<string> FormatArguments(string code)
    {
        if (code == AudioM4aCode)
            return ["-f", "ba[ext=m4a]/ba", "-x", "--audio-format", "m4a"];
        if (code == AudioMp3Code)
            return ["-f", "ba", "-x", "--audio-format", "mp3", "--audio-quality", "0"];
        if (code.StartsWith('v') && int.TryParse(code.AsSpan(1), out int height))
            return ["-f", $"bv*[height<={height}]+ba/b[height<={height}]/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"];
        return ["-f", "bv*+ba/b", "-S", "res,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4"];
    }

    /// <summary>Wert für <c>--proxy</c> aus den Einstellungen; null = Systemvorgabe (yt-dlp liest sie selbst).</summary>
    public static string? Proxy { get; set; }

    internal static void AddHeaders(List<string> args, string? referrer, string? userAgent)
    {
        if (Proxy is { } proxy)
            args.AddRange(["--proxy", proxy]);
        if (!string.IsNullOrWhiteSpace(referrer))
            args.AddRange(["--referer", referrer]);
        if (!string.IsNullOrWhiteSpace(userAgent))
            args.AddRange(["--user-agent", userAgent]);
    }

    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string exe, IEnumerable<string> args, CancellationToken token)
    {
        using var process = Process.Start(StartInfo(exe, args)) ?? throw new DownloadException(L.T("yt-dlp konnte nicht gestartet werden."));
        using var kill = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>Letzte „ERROR:“-Zeile von yt-dlp, ohne Präfix.</summary>
    internal static string? ErrorText(string stderr) =>
        stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) is { } line
            ? line["ERROR:".Length..].Trim()
            : null;

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private sealed record Format(string Ext, string VCodec, string ACodec, int Height, long? FileSize, double? Tbr)
    {
        public bool HasVideo => VCodec is not ("none" or "");
        public bool HasAudio => ACodec is not ("none" or "");
        public bool IsAudioOnly => HasAudio && !HasVideo;
        public bool IsH264 => VCodec.StartsWith("avc", StringComparison.OrdinalIgnoreCase) || VCodec.StartsWith("h264", StringComparison.OrdinalIgnoreCase);
        public double Bitrate => Tbr ?? 0;
        public string Codec => VCodec.Split('.')[0] switch { "vp09" or "vp9" => "VP9", "av01" => "AV1", var c => c.ToUpperInvariant() };

        public long? Size(double? duration) =>
            FileSize ?? (Tbr is double tbr && duration is double d ? (long)(tbr * 1000 / 8 * d) : null);

        public static Format From(JsonElement e) => new(
            Text(e, "ext") ?? "",
            Text(e, "vcodec") ?? (Text(e, "ext") is "mp3" or "m4a" or "opus" ? "none" : ""),
            Text(e, "acodec") ?? "",
            e.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0,
            (long?)(Number(e, "filesize") ?? Number(e, "filesize_approx")),
            Number(e, "tbr"));
    }

    internal static long? ParseLong(string text) =>
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (long)d
        : null;
}
