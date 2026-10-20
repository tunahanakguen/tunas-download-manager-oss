using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace TunasDM.Core.Browser;

/// <summary>
/// Meldet TunasDM.Host.exe bei Chrome, Edge und Firefox an: Manifest-Datei schreiben und unter
/// HKCU\Software\…\NativeMessagingHosts\com.tunas.dm eintragen (nur für den aktuellen Benutzer).
/// Firefox braucht ein eigenes Manifest (allowed_extensions statt allowed_origins).
/// </summary>
public static class NativeHostRegistration
{
    public const string FirefoxKey = @"Software\Mozilla\NativeMessagingHosts";

    public static readonly IReadOnlyDictionary<string, string> Browsers = new Dictionary<string, string>
    {
        ["Chrome"] = @"Software\Google\Chrome\NativeMessagingHosts",
        ["Edge"] = @"Software\Microsoft\Edge\NativeMessagingHosts",
        ["Firefox"] = FirefoxKey,
    };

    public static string CreateManifest(string hostPath) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["name"] = BrowserProtocol.HostName,
        ["description"] = "TUNAS Download Manager",
        ["path"] = hostPath,
        ["type"] = "stdio",
        ["allowed_origins"] = new[] { $"chrome-extension://{BrowserProtocol.ExtensionId}/" },
    }, new JsonSerializerOptions { WriteIndented = true });

    public static string CreateFirefoxManifest(string hostPath) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["name"] = BrowserProtocol.HostName,
        ["description"] = "TUNAS Download Manager",
        ["path"] = hostPath,
        ["type"] = "stdio",
        ["allowed_extensions"] = new[] { BrowserProtocol.FirefoxExtensionId },
    }, new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Schreibt die Manifeste und Registry-Einträge; ändert nur, was abweicht.</summary>
    [SupportedOSPlatform("windows")]
    public static void Register(string hostPath, string manifestFolder)
    {
        Directory.CreateDirectory(manifestFolder);
        string chromium = WriteIfChanged(Path.Combine(manifestFolder, BrowserProtocol.HostName + ".json"), CreateManifest(hostPath));
        string firefox = WriteIfChanged(Path.Combine(manifestFolder, BrowserProtocol.HostName + ".firefox.json"), CreateFirefoxManifest(hostPath));

        foreach (var keyPath in Browsers.Values)
        {
            string manifestPath = keyPath == FirefoxKey ? firefox : chromium;
            using var key = Registry.CurrentUser.CreateSubKey($@"{keyPath}\{BrowserProtocol.HostName}");
            if (key.GetValue(null) as string != manifestPath)
                key.SetValue(null, manifestPath);
        }
    }

    private static string WriteIfChanged(string path, string content)
    {
        if (!File.Exists(path) || File.ReadAllText(path) != content)
            File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Dateien, die TunasDM.Host.exe zum Laufen braucht: im Entwicklungsbau framework-abhängig (ohne Core),
    /// in der installierten Fassung nur die Exe als eigenständige Einzeldatei.
    /// </summary>
    public static readonly string[] HostFiles =
        ["TunasDM.Host.dll", "TunasDM.Host.exe", "TunasDM.Host.runtimeconfig.json", "TunasDM.Host.deps.json"];

    /// <summary>Neben der Host-Kopie: vollständiger Pfad zu TunasDM.exe (der Host startet und prüft damit die App).</summary>
    public const string AppPathFile = "TunasDM.app.txt";

    /// <summary>
    /// Kopiert den Host in einen festen Ordner und gibt den Pfad zur Kopie zurück. Die Browser starten dann diese Kopie:
    /// Ein Update oder Neubau der App entfernt die angemeldete Datei nicht kurzzeitig – Chrome merkt sich ein
    /// „Specified native messaging host not found“ sonst bis zum Neustart. Läuft der Host gerade (Datei gesperrt),
    /// bleibt die bisherige Kopie; sie funktioniert weiter. Klappt gar nichts, gilt der Host neben der App.
    /// </summary>
    public static string InstallHost(string appFolder, string targetFolder)
    {
        string source = Path.Combine(appFolder, "TunasDM.Host.exe");
        string target = Path.Combine(targetFolder, "TunasDM.Host.exe");
        // Was neben der App liegt, wird kopiert; Reste der anderen Bauart werden entfernt.
        var files = HostFiles.Where(f => File.Exists(Path.Combine(appFolder, f))).ToList();
        if (!files.Contains("TunasDM.Host.exe"))
            return source;
        try
        {
            Directory.CreateDirectory(targetFolder);
            // Die DLL zuerst: ist sie gesperrt, läuft der Host, und es wird gar nichts gemischt.
            foreach (string name in files)
            {
                string from = Path.Combine(appFolder, name);
                string to = Path.Combine(targetFolder, name);
                if (SameFile(from, to))
                    continue;
                string temp = to + ".neu";
                File.Copy(from, temp, overwrite: true);
                File.Move(temp, to, overwrite: true);
            }
            foreach (string name in HostFiles.Except(files))
                File.Delete(Path.Combine(targetFolder, name));
            string appPath = Path.Combine(appFolder, "TunasDM.exe");
            string appFile = Path.Combine(targetFolder, AppPathFile);
            if (!File.Exists(appFile) || File.ReadAllText(appFile).Trim() != appPath)
                File.WriteAllText(appFile, appPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gesperrt oder kein Zugriff: vorhandene Kopie weiter nutzen.
        }
        foreach (string name in HostFiles)
        {
            try
            {
                File.Delete(Path.Combine(targetFolder, name + ".neu")); // Rest einer abgebrochenen Kopie
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return files.All(f => File.Exists(Path.Combine(targetFolder, f)))
            && HostFiles.Except(files).All(f => !File.Exists(Path.Combine(targetFolder, f))) ? target : source;
    }

    private static bool SameFile(string a, string b)
    {
        if (!File.Exists(b))
            return false;
        var x = new FileInfo(a);
        var y = new FileInfo(b);
        return x.Length == y.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>Für welche Browser der Host eingetragen ist.</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<string> RegisteredBrowsers()
    {
        var result = new List<string>();
        foreach (var (browser, keyPath) in Browsers)
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{keyPath}\{BrowserProtocol.HostName}");
            if (key?.GetValue(null) is string path && File.Exists(path))
                result.Add(browser);
        }
        return result;
    }
}
