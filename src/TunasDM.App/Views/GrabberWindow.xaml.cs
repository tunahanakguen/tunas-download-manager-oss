using System.Windows;
using System.Windows.Controls;
using TunasDM.Core;
using TunasDM.Core.Grabber;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

/// <summary>Site-Grabber: Startseite, Dateitypen, Tiefe; danach die gefundenen Dateien auswählen.</summary>
public partial class GrabberWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly (string Name, string Extensions)[] Presets =
    [
        (L.T("Alle Dateien (keine Seiten)"), ""),
        (L.T("Bilder"), "jpg jpeg png gif webp svg bmp tif tiff avif"),
        (L.T("Dokumente"), "pdf doc docx xls xlsx ppt pptx odt ods odp rtf txt epub csv"),
        (L.T("Audio"), "mp3 m4a aac flac wav ogg opus wma"),
        (L.T("Video"), "mp4 mkv webm avi mov m4v wmv flv"),
        (L.T("Archive und Programme"), "zip rar 7z tar gz bz2 xz iso exe msi"),
        (L.T("Eigene Auswahl"), ""),
    ];

    private readonly Func<ProxySettings> _proxy;
    private CancellationTokenSource? _running;
    private bool _closed;

    public GrabberWindow(string? startUrl, Func<ProxySettings> proxy)
    {
        InitializeComponent();
        _proxy = proxy;
        PresetBox.ItemsSource = Presets.Select(p => p.Name).ToList();
        PresetBox.SelectedIndex = 0;
        DepthBox.ItemsSource = new[]
        {
            L.T("0 – nur die Startseite"), L.T("1 – und die verlinkten Seiten"),
            L.F("{0} Ebenen", 2), L.F("{0} Ebenen", 3), L.F("{0} Ebenen", 4), L.F("{0} Ebenen", 5),
        };
        DepthBox.SelectedIndex = 1;
        if (startUrl is not null)
            UrlBox.Text = startUrl;
        Closing += (_, _) => { _closed = true; _running?.Cancel(); };
    }

    public IReadOnlyList<GrabbedFile> Files { get; private set; } = [];

    public Uri? StartUrl { get; private set; }

    private void OnUrlChanged(object sender, TextChangedEventArgs e) => StartButton.IsEnabled = _running is null && ParseUrl() is not null;

    private Uri? ParseUrl()
    {
        string text = UrlBox.Text.Trim();
        if (text.Length > 0 && !text.Contains("://"))
            text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && url.Host.Length > 0 ? url : null;
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        int index = PresetBox.SelectedIndex;
        if (index < 0 || index == Presets.Length - 1)
        {
            ExtensionsBox.Focus();
            return;
        }
        ExtensionsBox.Text = Presets[index].Extensions;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (ParseUrl() is not { } url)
            return;
        StartUrl = url;
        var options = new GrabberOptions(url)
        {
            Depth = Math.Max(0, DepthBox.SelectedIndex),
            Extensions = Category.Split(ExtensionsBox.Text),
            SameHostOnly = SameHostBox.IsChecked == true,
            BelowStartFolderOnly = BelowBox.IsChecked == true,
            RespectRobots = RobotsBox.IsChecked == true,
            MaxPages = (int)Math.Clamp(MaxPagesBox.Value ?? 200, 1, 5000),
        };

        _running = new CancellationTokenSource();
        Form.IsEnabled = false;
        StartButton.IsEnabled = false;
        CancelButton.Content = L.T("Stopp");
        CancelButton.IsCancel = false;
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = L.T("Startseite wird gelesen …");
        var progress = new Progress<GrabberProgress>(p =>
        {
            ProgressText.Text = L.F("{0} Seiten gelesen, {1} Dateien gefunden", p.PagesVisited, p.FilesFound);
            CurrentText.Text = p.Current?.ToString() ?? "";
        });
        try
        {
            using var client = Download.CreateHttpClient(_proxy());
            Files = await new SiteGrabber(client, options).RunAsync(progress, _running.Token);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or DownloadException or TaskCanceledException)
        {
            ShowError(L.T("Die Startseite ließ sich nicht lesen:") + " " + ex.Message);
            return;
        }
        finally
        {
            _running = null;
        }
        if (_closed)
            return; // Fenster während der Suche geschlossen

        if (Files.Count == 0)
        {
            ShowError(L.T("Keine passenden Dateien gefunden. Mehr Tiefe, andere Dateitypen oder ohne „Nur Seiten dieser Website“ versuchen."));
            return;
        }
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        Form.IsEnabled = true;
        StartButton.IsEnabled = ParseUrl() is not null;
        CancelButton.Content = L.T("Abbrechen");
        CancelButton.IsCancel = true;
        CancelButton.IsEnabled = true;
    }

    /// <summary>Während der Suche: Stopp behält das bisher Gefundene. Sonst schließen.</summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_running is { } running)
        {
            CancelButton.IsEnabled = false;
            ProgressText.Text += " – " + L.T("wird angehalten …");
            running.Cancel();
            return;
        }
        Close();
    }
}
