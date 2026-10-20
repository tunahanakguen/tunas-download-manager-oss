namespace TunasDM.Core;

public sealed record DownloadRequest(Uri Url)
{
    public string TargetDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>Gewünschter Dateiname; sonst aus Content-Disposition oder URL.</summary>
    public string? FileName { get; init; }

    public string? Referrer { get; init; }
    public string? Cookies { get; init; }
    public string? UserAgent { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Vorhandene Datei gleichen Namens am Ende ersetzen statt „name (2).ext“ (Sync).</summary>
    public bool ReplaceExisting { get; init; }
}

/// <summary>Ergebnis der Sync-Prüfung: geändert ja/nein und die aktuellen Kennungen des Servers.</summary>
public sealed record ChangeCheck(bool Changed, string? ETag, DateTimeOffset? LastModified, long Size);

public sealed class DownloadOptions
{
    public int MaxConnections { get; set; } = 8;

    /// <summary>Ein Segment wird nur geteilt, wenn beide Hälften mindestens so groß sind.</summary>
    public long MinSegmentSize { get; set; } = 1024 * 1024;

    public int BufferSize { get; set; } = 64 * 1024;
    public TimeSpan SaveInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Fehlversuche hintereinander, bevor eine Verbindung aufgibt.</summary>
    public int MaxRetries { get; set; } = 5;

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>So viele bereits geladene Bytes werden beim Fortsetzen erneut angefragt und verglichen.</summary>
    public int VerifyBytes { get; set; } = 4096;

    /// <summary>Für Server, die das Änderungsdatum bei jeder Anfrage neu setzen.</summary>
    public bool IgnoreModificationTimeChanges { get; set; }

    /// <summary>Langsame Verbindungen abbrechen; ihr Rest wird neu verteilt (neue Verbindung, oft andere Route).</summary>
    public bool DropSlowConnections { get; set; } = true;

    /// <summary>Langsam heißt: weniger als dieser Anteil vom Durchschnitt der übrigen Verbindungen.</summary>
    public double SlowConnectionRatio { get; set; } = 0.3;

    /// <summary>So lange muss eine Verbindung laufen und so lange langsam sein, bevor sie abgebrochen wird.</summary>
    public TimeSpan SlowConnectionGrace { get; set; } = TimeSpan.FromSeconds(5);

    internal long EffectiveMinSegmentSize => Math.Max(MinSegmentSize, 2L * BufferSize);
}

/// <summary>Gespeicherter Stand eines Downloads (Datei neben dem Download).</summary>
public sealed class DownloadState
{
    public int Version { get; set; } = 1;
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";

    /// <summary>-1 = unbekannt.</summary>
    public long TotalSize { get; set; } = -1;

    public bool SupportsRanges { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string? Referrer { get; set; }
    public string? Cookies { get; set; }
    public string? UserAgent { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>Siehe <see cref="DownloadRequest.ReplaceExisting"/>; im Stand, damit es auch nach Neustart gilt.</summary>
    public bool ReplaceExisting { get; set; }

    public List<SegmentState> Segments { get; set; } = new();
}

public sealed class SegmentState
{
    public long Start { get; set; }
    public long Position { get; set; }
    public long End { get; set; }
}

/// <summary>Ergebnis der Vorab-Abfrage: Name, Größe (-1 = unbekannt), Fortsetzen möglich.</summary>
public sealed record RemoteFileInfo(Uri FinalUrl, string FileName, long Size, bool SupportsRanges, string? ContentType);

public sealed record SegmentView(long Start, long Position, long End, bool Active);

public sealed record DownloadProgress(
    string? FileName,
    long TotalSize,
    long Downloaded,
    int ActiveConnections,
    IReadOnlyList<SegmentView> Segments);

public class DownloadException : Exception
{
    public DownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Die Datei auf dem Server ist nicht mehr dieselbe wie beim Start.</summary>
public sealed class ServerFileChangedException : DownloadException
{
    public ServerFileChangedException(string message) : base(message) { }
}

public sealed class HttpStatusException : DownloadException
{
    public HttpStatusException(System.Net.HttpStatusCode statusCode)
        : base(L.F("Server antwortet mit {0} {1}.", (int)statusCode, statusCode)) => StatusCode = statusCode;

    public System.Net.HttpStatusCode StatusCode { get; }

    public bool IsTransient => (int)StatusCode is 408 or 429 or >= 500;
}
