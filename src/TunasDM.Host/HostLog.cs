namespace TunasDM.Host;

/// <summary>Kurzes Protokoll in %LocalAppData%\TUNAS Download Manager\host.log (stdout gehört dem Browser).</summary>
internal static class HostLog
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TUNAS Download Manager", "host.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 256 * 1024)
                File.Delete(FilePath);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Protokoll ist Nebensache.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
