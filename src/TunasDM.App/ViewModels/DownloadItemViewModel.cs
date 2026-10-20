using CommunityToolkit.Mvvm.ComponentModel;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.ViewModels;

/// <summary>Eine Zeile der Download-Liste; wird vom Timer im Hauptfenster aufgefrischt.</summary>
public sealed class DownloadItemViewModel : ObservableObject
{
    private SpeedMeter _meter = new();
    private bool _wasActive;

    public DownloadItemViewModel(DownloadItem item)
    {
        Item = item;
        Progress = item.Progress;
    }

    public DownloadItem Item { get; }
    public DownloadProgress Progress { get; private set; }
    public double Speed { get; private set; }

    public DownloadStatus Status => Item.Status;
    public bool IsActive => Item.IsActive;
    public bool IsCompleted => Item.Status == DownloadStatus.Completed;
    public string Name => Item.DisplayName;
    /// <summary>Zum Anzeigen; ein FTP-Passwort steht als ***.</summary>
    public string Url => Secrets.MaskPassword(Item.Url);
    public string Category => Item.Category;
    public string? Error => Item.Error;
    public DateTimeOffset AddedAt => Item.AddedAt;
    public long TotalSize => Progress.TotalSize;
    public string? FilePath => Item.FilePath;

    public Wpf.Ui.Controls.SymbolRegular Symbol => Item.Category == TunasDM.Core.Management.Category.General
        ? Wpf.Ui.Controls.SymbolRegular.Document24
        : FilterViewModel.SymbolFor(Item.Category);

    /// <summary>Zweite Zeile in der Liste: Größe und je nach Zustand Verbindungen, Restzeit, Fehler.</summary>
    public string SubText
    {
        get
        {
            string size = Progress.TotalSize >= 0 ? ByteSize.Format(Progress.TotalSize) : L.T("Größe unbekannt");
            return Item.Status switch
            {
                DownloadStatus.Downloading => $"{size}  ·  {Connections}"
                    + (EtaText.Length > 0 ? "  ·  " + L.F("noch {0}", EtaText) : ""),
                DownloadStatus.Connecting => $"{size}  ·  {L.T("verbinde …")}",
                DownloadStatus.Queued => $"{size}  ·  {L.T("in der Warteschlange")}",
                DownloadStatus.Paused => $"{size}  ·  {L.T("pausiert")}",
                DownloadStatus.Completed => $"{size}  ·  {Item.CompletedAt?.ToString("g", L.Culture) ?? AddedText}",
                DownloadStatus.Error => Item.Error ?? L.T("Fehler"),
                _ => size,
            };
        }
    }

    /// <summary>Text unter dem Fortschrittsbalken.</summary>
    public string ProgressText => Item.Status switch
    {
        DownloadStatus.Downloading when Progress.TotalSize > 0 => $"{Percent:0} %  ·  {SpeedText}",
        DownloadStatus.Downloading => $"{ByteSize.Format(Progress.Downloaded)}  ·  {SpeedText}",
        DownloadStatus.Completed => L.T("Fertig"),
        DownloadStatus.Error => L.T("Fehler"),
        _ => Percent > 0 ? $"{Percent:0} %" : StatusText,
    };

    public bool ShowsBar => Item.Status is DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Connecting
        || (Item.Status is DownloadStatus.Queued or DownloadStatus.Error && Percent > 0);

    public double Percent => Progress.TotalSize > 0 ? Math.Clamp(Progress.Downloaded * 100.0 / Progress.TotalSize, 0, 100) : 0;

    public string SizeText => Progress.TotalSize >= 0 ? ByteSize.Format(Progress.TotalSize) : L.T("unbekannt");

    public string StatusText => Item.Status switch
    {
        DownloadStatus.Queued => L.T("In Warteschlange"),
        DownloadStatus.Connecting => L.T("Verbinde …"),
        DownloadStatus.Downloading when Progress.TotalSize > 0 => $"{Percent:0.0} %",
        DownloadStatus.Downloading => ByteSize.Format(Progress.Downloaded),
        DownloadStatus.Paused when Percent > 0 => L.T("Pausiert") + $" · {Percent:0} %",
        DownloadStatus.Paused => L.T("Pausiert"),
        DownloadStatus.Completed => L.T("Fertig"),
        DownloadStatus.Error => L.T("Fehler"),
        _ => "",
    };

    private string Connections => L.F(Progress.ActiveConnections == 1 ? "{0} Verbindung" : "{0} Verbindungen", Progress.ActiveConnections);

    public string SpeedText => IsActive && Speed > 0 ? ByteSize.Format(Speed) + "/s" : "";

    public string EtaText
    {
        get
        {
            if (!IsActive || Speed <= 0 || Progress.TotalSize <= 0)
                return "";
            var eta = TimeSpan.FromSeconds((Progress.TotalSize - Progress.Downloaded) / Speed);
            return eta.TotalHours >= 1 ? $"{(int)eta.TotalHours} h {eta.Minutes:00} min"
                : eta.TotalMinutes >= 1 ? $"{eta.Minutes} min {eta.Seconds:00} s"
                : $"{Math.Max(1, eta.Seconds)} s";
        }
    }

    public string AddedText => Item.AddedAt.ToString("g", L.Culture);

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Progress.TotalSize > 0)
                parts.Add(L.F("{0} von {1}", ByteSize.Format(Progress.Downloaded), ByteSize.Format(Progress.TotalSize)));
            else if (Progress.Downloaded > 0)
                parts.Add(ByteSize.Format(Progress.Downloaded));
            if (IsActive)
                parts.Add(Connections);
            if (Item.Status != DownloadStatus.Queued)
                parts.Add(Item.SupportsRanges ? L.T("fortsetzbar") : L.T("nicht fortsetzbar"));
            if (Item.Error is { } error && Item.Status == DownloadStatus.Error)
                parts.Add(error);
            return string.Join("  ·  ", parts);
        }
    }

    public string Location => Item.FilePath ?? Path.Combine(Item.Directory, Item.DisplayName);

    public void Refresh()
    {
        Progress = Item.Progress;
        if (Item.IsActive)
        {
            if (!_wasActive)
                _meter = new SpeedMeter();
            Speed = _meter.Update(Progress.Downloaded);
        }
        else
        {
            Speed = 0;
        }
        _wasActive = Item.IsActive;
        OnPropertyChanged(string.Empty);
    }
}
