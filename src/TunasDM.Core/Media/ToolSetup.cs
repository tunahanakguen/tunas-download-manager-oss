using System.IO.Compression;
using System.Security.Cryptography;

using TunasDM.Core;

namespace TunasDM.Core.Media;

/// <summary>Fortschritt der Einrichtung: welches Werkzeug, wie viel davon geladen ist.</summary>
public sealed record ToolSetupProgress(string Tool, long Received, long? Total);

/// <summary>
/// Lädt yt-dlp und ffmpeg (Builds des yt-dlp-Projekts) von GitHub in den Datenordner der App.
/// Beide Dateien werden gegen die Prüfsummen der jeweiligen Veröffentlichung geprüft; ffmpeg kommt als ZIP,
/// davon werden nur ffmpeg.exe und ffprobe.exe entpackt. Fertige Dateien ersetzen die alten erst am Schluss.
/// </summary>
public static class ToolSetup
{
    public const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    public const string YtDlpSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
    public const string FfmpegUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    public const string FfmpegSums = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";

    public static bool NeedsSetup => MediaTools.YtDlpPath is null || MediaTools.FfmpegPath is null;

    /// <summary>Richtet ein, was fehlt (oder alles, wenn <paramref name="force"/>).</summary>
    public static async Task InstallAsync(HttpClient client, string folder, IProgress<ToolSetupProgress>? progress,
        CancellationToken cancellationToken, bool force = false)
    {
        Directory.CreateDirectory(folder);
        if (force || MediaTools.YtDlpPath is null)
        {
            string temp = Path.Combine(folder, "yt-dlp.exe.download");
            try
            {
                string expected = await ExpectedHashAsync(client, YtDlpSums, "yt-dlp.exe", cancellationToken).ConfigureAwait(false);
                await DownloadAsync(client, YtDlpUrl, temp, expected, "yt-dlp", progress, cancellationToken).ConfigureAwait(false);
                File.Move(temp, Path.Combine(folder, "yt-dlp.exe"), overwrite: true);
            }
            finally
            {
                File.Delete(temp);
            }
        }

        if (force || MediaTools.FfmpegPath is null)
        {
            string zip = Path.Combine(folder, "ffmpeg.zip.download");
            try
            {
                string name = Path.GetFileName(new Uri(FfmpegUrl).AbsolutePath);
                string expected = await ExpectedHashAsync(client, FfmpegSums, name, cancellationToken).ConfigureAwait(false);
                await DownloadAsync(client, FfmpegUrl, zip, expected, "ffmpeg", progress, cancellationToken).ConfigureAwait(false);
                ExtractFfmpeg(zip, folder);
            }
            finally
            {
                File.Delete(zip);
            }
        }
    }

    /// <summary>Prüfsumme einer Datei aus einer Liste im Format „hash  name“ (sha256sum).</summary>
    public static string? FindHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[])[' ', '\t', '*'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*') == fileName && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static async Task<string> ExpectedHashAsync(HttpClient client, string url, string fileName, CancellationToken token)
    {
        string sums = await client.GetStringAsync(url, token).ConfigureAwait(false);
        return FindHash(sums, fileName) ?? throw new DownloadException(L.F("Keine Prüfsumme für {0} veröffentlicht.", fileName));
    }

    private static async Task DownloadAsync(HttpClient client, string url, string target, string expectedHash, string tool,
        IProgress<ToolSetupProgress>? progress, CancellationToken token)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DownloadException(L.F("{0}: Server antwortet mit {1}.", tool, (int)response.StatusCode));
        long? total = response.Content.Headers.ContentLength;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long received = 0;
            long lastReport = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                sha.AppendData(buffer, 0, read);
                received += read;
                if (received - lastReport >= 512 * 1024)
                {
                    lastReport = received;
                    progress?.Report(new ToolSetupProgress(tool, received, total));
                }
            }
            progress?.Report(new ToolSetupProgress(tool, received, total));
        }
        string actual = Convert.ToHexStringLower(sha.GetHashAndReset());
        if (actual != expectedHash)
            throw new DownloadException(L.F("{0}: Prüfsumme stimmt nicht – Datei verworfen.", tool));
    }

    /// <summary>Nur ffmpeg.exe und ffprobe.exe aus dem Ordner bin/ des Archivs.</summary>
    internal static void ExtractFfmpeg(string zipPath, string folder)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (string name in (string[])["ffmpeg.exe", "ffprobe.exe"])
        {
            var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("/bin/" + name, StringComparison.OrdinalIgnoreCase))
                ?? throw new DownloadException(L.F("{0} fehlt im ffmpeg-Archiv.", name));
            string temp = Path.Combine(folder, name + ".download");
            entry.ExtractToFile(temp, overwrite: true);
            File.Move(temp, Path.Combine(folder, name), overwrite: true);
        }
    }
}
