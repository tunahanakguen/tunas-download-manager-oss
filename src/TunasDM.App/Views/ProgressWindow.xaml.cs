using System.Windows;
using System.Windows.Threading;
using TunasDM.App.Services;
using TunasDM.App.ViewModels;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

/// <summary>Fortschritt eines Downloads mit Segmentbalken und Verbindungstabelle.</summary>
public partial class ProgressWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadManager _manager;
    private readonly DownloadItemViewModel _row;
    private readonly DispatcherTimer _timer;

    public ProgressWindow(DownloadItem item, DownloadManager manager)
    {
        InitializeComponent();
        _manager = manager;
        _row = new DownloadItemViewModel(item);
        DataContext = _row;
        // Videos laufen über yt-dlp: keine eigenen Verbindungen, die man zeigen könnte.
        bool media = item.Kind == DownloadKind.Media;
        ConnectionsPanel.Visibility = media ? Visibility.Collapsed : Visibility.Visible;
        MediaHint.Visibility = media ? Visibility.Visible : Visibility.Collapsed;

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        Refresh();
    }

    public DownloadItem Item => _row.Item;

    private void Refresh()
    {
        _row.Refresh();
        // Prozent vorne im Titel, damit man den Stand auch in der Taskleiste sieht.
        Title = _row.IsActive && _row.TotalSize > 0 ? $"{_row.Percent:0} % {_row.Name}" : _row.Name;
        int number = 1;
        Connections.ItemsSource = _row.Progress.Segments.Select(s => new ConnectionRow(number++, s)).ToList();
        ToggleButton.Content = L.T(_row.IsActive ? "Pausieren" : "Fortsetzen");
        ToggleButton.IsEnabled = _row.IsActive || _row.Item.CanStart;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_row.IsActive)
            _manager.Pause(_row.Item);
        else
            _manager.Start(_row.Item);
        Refresh();
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_row.FilePath is { } path && File.Exists(path))
            Shell.ShowInFolder(path);
        else
            Shell.Open(_row.Item.Directory);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed record ConnectionRow(int Number, SegmentView Segment)
    {
        public string StartText => ByteSize.Format(Segment.Start);
        public string DoneText => ByteSize.Format(Segment.Position - Segment.Start);
        public string EndText => Segment.End == long.MaxValue ? L.T("offen") : ByteSize.Format(Segment.End);
        public string State => L.T(Segment.Position >= Segment.End ? "fertig" : Segment.Active ? "lädt" : "wartet");
    }
}
