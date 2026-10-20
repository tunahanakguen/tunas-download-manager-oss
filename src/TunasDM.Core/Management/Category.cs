namespace TunasDM.Core.Management;

/// <summary>Kategorie: Dateiendungen bestimmen die Kategorie, die Kategorie den Zielordner.</summary>
public sealed class Category
{
    public const string General = "Allgemein";

    public string Name { get; set; } = General;

    /// <summary>Zum Anzeigen: eingebaute Kategorien übersetzt, eigene unverändert.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => L.T(Name);
    public List<string> Extensions { get; set; } = new();

    /// <summary>Eigener Ordner; leer = Unterordner mit dem Kategorienamen im Download-Ordner.</summary>
    public string? Folder { get; set; }

    public string ResolveFolder(string downloadsFolder) =>
        !string.IsNullOrWhiteSpace(Folder) ? Folder
        : Name == General ? downloadsFolder
        : Path.Combine(downloadsFolder, Name);

    public bool Matches(string fileName)
    {
        string extension = Path.GetExtension(fileName).TrimStart('.');
        return extension.Length > 0 && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    public static List<Category> Defaults() =>
    [
        new() { Name = General },
        new() { Name = "Video", Extensions = Split("mp4 mkv avi mov wmv flv webm m4v mpg mpeg 3gp ts m2ts vob ogv") },
        new() { Name = "Musik", Extensions = Split("mp3 wav flac aac m4a ogg opus wma aif aiff mid") },
        new() { Name = "Dokumente", Extensions = Split("pdf doc docx xls xlsx ppt pptx odt ods odp txt rtf epub csv") },
        new() { Name = "Programme", Extensions = Split("exe msi msix appx apk dmg pkg deb rpm bat cmd") },
        new() { Name = "Archive", Extensions = Split("zip rar 7z tar gz bz2 xz zst iso img cab") },
        new() { Name = "Bilder", Extensions = Split("jpg jpeg png gif webp bmp tif tiff svg heic raw psd") },
    ];

    /// <summary>Erste passende Kategorie, sonst „Allgemein“.</summary>
    public static Category For(string fileName, IReadOnlyList<Category> categories) =>
        categories.FirstOrDefault(c => c.Name != General && c.Matches(fileName))
        ?? categories.FirstOrDefault(c => c.Name == General)
        ?? new Category();

    public static List<string> Split(string extensions) =>
        extensions.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.TrimStart('*', '.').ToLowerInvariant())
            .Distinct()
            .ToList();
}
