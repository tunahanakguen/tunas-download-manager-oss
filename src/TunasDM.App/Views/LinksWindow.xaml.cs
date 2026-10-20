using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using TunasDM.Core.Browser;
using TunasDM.Core.Management;
using TunasDM.Core;

namespace TunasDM.App.Views;

/// <summary>„Alle Links mit TUNAS laden“: Links der Seite auswählen.</summary>
public partial class LinksWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly List<LinkRow> _rows;
    private readonly AppSettings _settings;

    public LinksWindow(IEnumerable<LinkInfo> links, string? page, AppSettings settings, bool selectAll = false)
    {
        InitializeComponent();
        _settings = settings;
        _rows = links
            .Select(l => Uri.TryCreate(l.Url, UriKind.Absolute, out var uri) && DownloadSchemes.IsSupported(uri) ? (uri, l.Text) : default)
            .Where(x => x.uri is not null)
            .DistinctBy(x => x.uri.ToString())
            .Select(x => new LinkRow(x.uri, string.IsNullOrWhiteSpace(x.Text) ? DownloadItem.NameFromUrl(x.uri.ToString()) : x.Text.Trim()))
            .ToList();
        List.ItemsSource = _rows;
        PageText.Text = page ?? "";
        ApplyFilter();
        if (selectAll)
            SetAll(true); // importierte Liste: alles war gewollt
        foreach (var row in _rows)
            row.PropertyChanged += (_, _) => UpdateHeader();
    }

    public List<Uri> Selected { get; private set; } = [];
    public bool StartNow { get; private set; }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    /// <summary>Vorauswahl: Links auf Dateien mit bekannter (oder eingegebener) Endung.</summary>
    private void ApplyFilter()
    {
        var wanted = Category.Split(FilterBox.Text);
        var known = wanted.Count > 0 ? wanted : _settings.Categories.SelectMany(c => c.Extensions).ToList();
        foreach (var row in _rows)
        {
            string extension = Path.GetExtension(row.Url.AbsolutePath).TrimStart('.').ToLowerInvariant();
            row.Selected = extension.Length > 0 && known.Contains(extension);
        }
        UpdateHeader();
    }

    private void UpdateHeader() =>
        Header.Text = L.F("{0} Links gefunden, {1} ausgewählt", _rows.Count, _rows.Count(r => r.Selected));

    private void OnAll(object sender, RoutedEventArgs e) => SetAll(true);

    private void OnNone(object sender, RoutedEventArgs e) => SetAll(false);

    private void SetAll(bool value)
    {
        foreach (var row in _rows)
            row.Selected = value;
        UpdateHeader();
    }

    private void OnStart(object sender, RoutedEventArgs e) => Finish(true);

    private void OnQueue(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool startNow)
    {
        Selected = _rows.Where(r => r.Selected).Select(r => r.Url).ToList();
        StartNow = startNow;
        DialogResult = Selected.Count > 0;
    }

    public sealed partial class LinkRow(Uri url, string name) : ObservableObject
    {
        public Uri Url { get; } = url;
        public string Name { get; } = name;

        [ObservableProperty]
        private bool _selected;
    }
}
