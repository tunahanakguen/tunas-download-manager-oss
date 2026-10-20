using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using TunasDM.Core;
using TunasDM.Core.Updates;

namespace TunasDM.App.Services;

/// <summary>Update-Suche auf GitHub und Start des geprüften Setups.</summary>
internal static class Updater
{
    public static Version CurrentVersion { get; } =
        UpdateChecker.ParseVersion(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? new Version(0, 0, 0, 0);

    /// <summary>Mit dem Setup installiert (nicht aus dem Entwicklungsordner gestartet): nur dann ersetzt das Setup diese Fassung.</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static string DownloadFolder => Path.Combine(Logger.Folder, "updates");

    private static HttpClient CreateClient(ProxySettings proxy)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.All };
        proxy.ApplyTo(handler);
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"TUNAS-Download-Manager/{CurrentVersion.ToString(3)}");
        return client;
    }

    public static async Task<UpdateInfo?> CheckAsync(ProxySettings proxy, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(proxy);
        return await UpdateChecker.CheckAsync(client, UpdateChecker.LatestReleaseUri(UpdateChecker.DefaultRepository),
            CurrentVersion, cancellationToken);
    }

    public static async Task<string> DownloadAsync(UpdateInfo update, ProxySettings proxy, IProgress<(long, long)> progress,
        CancellationToken cancellationToken)
    {
        // Alte Setups wegräumen.
        if (Directory.Exists(DownloadFolder))
        {
            foreach (var old in Directory.GetFiles(DownloadFolder))
            {
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        using var client = CreateClient(proxy);
        return await UpdateChecker.DownloadAsync(client, update, DownloadFolder, progress, cancellationToken);
    }

    /// <summary>
    /// Startet das Setup mit sichtbarem Fortschritt und ohne Rückfragen. Es beendet TUNAS selbst sauber (<c>/exit</c>),
    /// übernimmt die bisherigen Optionen und startet die neue Fassung danach im Infobereich.
    /// </summary>
    public static void RunSetup(string setupPath) =>
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SP- /SUPPRESSMSGBOXES /NOCANCEL /NORESTART") { UseShellExecute = true });
}
