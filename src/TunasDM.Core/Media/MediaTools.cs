namespace TunasDM.Core.Media;

/// <summary>Findet yt-dlp und ffmpeg: neben dem Programm, im Datenordner der App oder im PATH.</summary>
public static class MediaTools
{
    public static string DataToolsFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TUNAS Download Manager", "tools");

    public static string? YtDlpPath => Find("yt-dlp.exe");

    public static string? FfmpegPath => Find("ffmpeg.exe");

    public static bool IsAvailable => YtDlpPath is not null;

    public static string? Find(string fileName)
    {
        var folders = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools"),
            DataToolsFolder,
        };
        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var folder in folders)
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Ungültiger PATH-Eintrag.
            }
        }
        return null;
    }
}
