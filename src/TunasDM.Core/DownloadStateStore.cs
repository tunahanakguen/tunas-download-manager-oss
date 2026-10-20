using System.Text.Json;

namespace TunasDM.Core;

/// <summary>Teil-Datei <c>name.ext.tunasdm</c> und Stand <c>name.ext.tunasdm.json</c> liegen neben dem Ziel.</summary>
public static class DownloadStateStore
{
    public const string TempExtension = ".tunasdm";
    public const string StateExtension = ".tunasdm.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string TempPathOf(string filePath) => filePath + TempExtension;

    public static string StatePathOf(string filePath) => filePath + StateExtension;

    /// <summary>Atomar und bis auf die Platte geschrieben, damit auch ein Stromausfall nichts zerstört.</summary>
    public static void Save(string statePath, DownloadState state)
    {
        string temp = statePath + ".tmp";
        // Cookies stehen neben dem Download im Ordner: nur verschlüsselt ablegen.
        // Über eine Kopie, weil laufende Verbindungen state.Cookies gleichzeitig lesen.
        var node = JsonSerializer.SerializeToNode(state, Json)!;
        node[nameof(DownloadState.Cookies)] = Secrets.Protect(state.Cookies);
        node[nameof(DownloadState.Url)] = Secrets.ProtectUrl(state.Url); // FTP-Passwort in der Adresse
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                node.WriteTo(writer);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, statePath, overwrite: true);
    }

    public static DownloadState Load(string statePath)
    {
        using var stream = File.OpenRead(statePath);
        var state = JsonSerializer.Deserialize<DownloadState>(stream, Json)
            ?? throw new DownloadException(L.F("Stand-Datei ist leer: {0}", statePath));
        state.Cookies = Secrets.Unprotect(state.Cookies);
        state.Url = Secrets.UnprotectUrl(state.Url);
        return state;
    }

    /// <summary>Sucht einen angefangenen Download derselben URL im Ordner.</summary>
    public static string? FindByUrl(string directory, Uri url)
    {
        if (!Directory.Exists(directory))
            return null;

        foreach (var file in Directory.EnumerateFiles(directory, "*" + StateExtension))
        {
            try
            {
                if (Load(file).Url == url.ToString())
                    return file;
            }
            catch (Exception ex) when (ex is IOException or JsonException or DownloadException)
            {
                // Beschädigte oder fremde Datei: überspringen.
            }
        }
        return null;
    }
}
