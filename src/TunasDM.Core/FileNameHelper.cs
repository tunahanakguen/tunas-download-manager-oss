namespace TunasDM.Core;

public static class FileNameHelper
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Name aus Content-Disposition, sonst aus der (weitergeleiteten) URL.</summary>
    public static string FromResponse(HttpResponseMessage response, Uri requestUri)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        string? name = disposition?.FileNameStar ?? disposition?.FileName;
        name = name?.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(name))
        {
            var uri = response.RequestMessage?.RequestUri ?? requestUri;
            name = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        }

        return Sanitize(string.IsNullOrWhiteSpace(name) ? "download" : name);
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        string cleaned = new string(chars).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            cleaned = "download";

        string stem = Path.GetFileNameWithoutExtension(cleaned);
        string extension = Path.GetExtension(cleaned);
        if (ReservedNames.Contains(stem))
            stem += "_";
        if (stem.Length + extension.Length > 200)
            stem = stem[..Math.Max(1, 200 - extension.Length)];
        return stem + extension;
    }

    /// <summary>„name (1).ext“, wenn der Name schon belegt ist – auch durch einen laufenden Download.</summary>
    public static string MakeUnique(string directory, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        string candidate = name;
        for (int i = 1; IsTaken(Path.Combine(directory, candidate)); i++)
            candidate = $"{stem} ({i}){extension}";
        return candidate;
    }

    private static bool IsTaken(string path) =>
        File.Exists(path)
        || File.Exists(DownloadStateStore.TempPathOf(path))
        || File.Exists(DownloadStateStore.StatePathOf(path));
}
