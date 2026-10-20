namespace TunasDM.App.Services;

public static class Logger
{
    private static readonly object Lock = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TUNAS Download Manager");

    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(Path.Combine(Folder, "log.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Protokoll ist nicht wichtig genug, um daran zu scheitern.
        }
    }
}
