namespace TunasDM.Core.Management;

public sealed class AppSettings
{
    public string DownloadsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Verbindungen pro Datei.</summary>
    public int MaxConnections { get; set; } = 8;

    /// <summary>Dateien, die die Warteschlange gleichzeitig lädt.</summary>
    public int MaxParallelDownloads { get; set; } = 3;

    /// <summary>Gesamtlimit in Bytes pro Sekunde; 0 = unbegrenzt.</summary>
    public long SpeedLimit { get; set; }

    public bool MonitorClipboard { get; set; } = true;
    public bool ShowFileInfoDialog { get; set; } = true;
    public bool ShowCompleteDialog { get; set; } = true;

    /// <summary>Beim Start eines Downloads sein Fortschrittsfenster öffnen.</summary>
    public bool ShowProgressDialog { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool IgnoreModificationTimeChanges { get; set; }

    /// <summary>Downloads aus Chrome und Edge übernehmen (Erweiterung muss installiert sein).</summary>
    public bool BrowserIntegration { get; set; } = true;

    /// <summary>„Video laden“-Symbol über Videos im Browser zeigen.</summary>
    public bool ShowVideoButton { get; set; } = true;

    /// <summary>Zuletzt gewählte Video-Qualität (z. B. „v1080“), wird im Dialog vorausgewählt.</summary>
    public string PreferredMediaFormat { get; set; } = "v1080";

    /// <summary>Beim Start (höchstens einmal am Tag) auf GitHub nach einer neuen Fassung suchen.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Oberflächensprache: „“ = wie Windows, „de“, „en“ (gilt ab dem nächsten Start).</summary>
    public string Language { get; set; } = "";

    /// <summary>Design: „“ = wie Windows (Standard), „dark“ oder „light“ (gilt ab dem nächsten Start).</summary>
    public string Theme { get; set; } = "";

    public DateTimeOffset? LastUpdateCheck { get; set; }

    /// <summary>„Diese Version überspringen“: wird nicht erneut angeboten.</summary>
    public string? SkippedUpdateVersion { get; set; }

    /// <summary>Seiten, deren Downloads der Browser selbst behält, z. B. „*.example.com“.</summary>
    public List<string> BrowserExcludedSites { get; set; } = new();

    public List<Category> Categories { get; set; } = Category.Defaults();

    public ProxyMode ProxyMode { get; set; } = ProxyMode.System;

    /// <summary>z. B. „proxy.firma.de:8080“ oder „socks5://127.0.0.1:1080“.</summary>
    public string? ProxyAddress { get; set; }

    public string? ProxyUser { get; set; }

    /// <summary>Verschlüsselt (DPAPI); lesen und setzen über <see cref="Proxy"/> und <see cref="SetProxyPassword"/>.</summary>
    public string? ProxyPasswordProtected { get; set; }

    public ProxySettings Proxy() => new(ProxyMode, ProxyAddress, ProxyUser, Secrets.Unprotect(ProxyPasswordProtected));

    public void SetProxyPassword(string? password) =>
        ProxyPasswordProtected = string.IsNullOrEmpty(password) ? null : Secrets.Protect(password);

    public DownloadOptions ToDownloadOptions() => new()
    {
        MaxConnections = Math.Clamp(MaxConnections, 1, 32),
        IgnoreModificationTimeChanges = IgnoreModificationTimeChanges,
    };

    public string FolderFor(string categoryName) =>
        (Categories.FirstOrDefault(c => c.Name == categoryName) ?? new Category()).ResolveFolder(DownloadsFolder);
}
