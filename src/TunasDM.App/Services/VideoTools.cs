using System.Net.Http;
using TunasDM.Core;
using TunasDM.Core.Media;

namespace TunasDM.App.Services;

/// <summary>Einrichtung von yt-dlp und ffmpeg; läuft höchstens einmal gleichzeitig (Optionen, Video-Fenster, Installer).</summary>
internal static class VideoTools
{
    private static readonly object Gate = new();
    private static Task? _running;

    /// <summary>Fortschritt aus dem Hintergrund; Empfänger wechseln selbst in den UI-Thread.</summary>
    public static event EventHandler<ToolSetupProgress>? Progress;

    public static bool IsRunning => _running is { IsCompleted: false };

    public static Task SetupAsync(ProxySettings proxy, bool force = false)
    {
        lock (Gate)
        {
            if (_running is { IsCompleted: false } running)
                return running;
            return _running = Task.Run(() => RunAsync(proxy, force));
        }
    }

    private static async Task RunAsync(ProxySettings proxy, bool force)
    {
        // Eigener Client: GitHub leitet auf seine Download-Server um, der Download-Client folgt Umleitungen nur selbst.
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.All };
        proxy.ApplyTo(handler);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TUNAS-Download-Manager");
        Logger.Write("Video-Werkzeuge werden eingerichtet …");
        try
        {
            await ToolSetup.InstallAsync(client, MediaTools.DataToolsFolder,
                new Progress<ToolSetupProgress>(p => Progress?.Invoke(null, p)), CancellationToken.None, force);
            Logger.Write("Video-Werkzeuge eingerichtet.");
        }
        catch (Exception ex)
        {
            Logger.Write("Video-Werkzeuge: " + ex.Message);
            throw;
        }
    }

    public static string Describe(ToolSetupProgress p) => p.Total is > 0 and long total
        ? L.F("{0} wird geladen … {1} % von {2:0} MB", p.Tool, p.Received * 100 / total, total / 1048576.0)
        : L.F("{0} wird geladen … {1:0} MB", p.Tool, p.Received / 1048576.0);
}
