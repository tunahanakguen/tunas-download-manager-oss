using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using TunasDM.Core;
using TunasDM.Core.Management;
using TunasDM.Core.Media;

namespace TunasDM.App.Views;

/// <summary>„Video laden“: Qualität wählen. Fragt yt-dlp nach den Formaten.</summary>
public partial class MediaWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IReadOnlyList<Uri> _candidates;
    private readonly string? _referrer;
    private readonly string? _userAgent;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _probe = new();

    /// <summary>Titel des Browser-Tabs; wird bei fremden Seiten zum Dateinamen.</summary>
    private readonly string? _pageTitle;
    private Uri? _url;
    private bool _folderEdited;
    private bool _updating;

    public MediaWindow(IReadOnlyList<Uri> candidates, string? titleHint, string? referrer, string? userAgent, AppSettings settings)
    {
        InitializeComponent();
        _candidates = candidates;
        _referrer = referrer;
        _userAgent = userAgent;
        _settings = settings;
        _pageTitle = titleHint;
        if (MediaProbe.CleanPageTitle(titleHint) is { } hint)
            TitleText.Text = hint;
        MetaText.Text = candidates.FirstOrDefault()?.Host ?? "";
        SetFolder(Category.For("x.mp4", settings.Categories));
        FolderBox.TextChanged += (_, _) => _folderEdited |= !_updating;
        Loaded += async (_, _) => await ProbeAsync();
        Closed += (_, _) => _probe.Cancel();
    }

    public NewDownload? Result { get; private set; }
    public bool StartNow { get; private set; }

    /// <summary>Warteschlangen für „Später laden“ (bei mehreren klappt eine Auswahl auf).</summary>
    public IReadOnlyList<DownloadQueue> Queues { get; init; } = [];

    /// <summary>
    /// Alle Kandidaten gleichzeitig abfragen (direkte Quelle, Player-Frame, Seite, erkannte Streams)
    /// und die beste Fassung zeigen – nicht einfach die erste, die antwortet.
    /// </summary>
    private async Task ProbeAsync()
    {
        if (!MediaTools.IsAvailable)
        {
            if (Services.VideoTools.IsRunning)
            {
                await SetupToolsAsync();
                return;
            }
            LoadingPanel.Visibility = Visibility.Collapsed;
            SetupPanel.Visibility = Visibility.Visible;
            return;
        }
        LoadingText.Text = L.T("Qualitäten werden abgefragt …");
        LoadingPanel.Visibility = Visibility.Visible;

        string? lastError = null;
        try
        {
            // Höchstens sechs yt-dlp-Prozesse; die ersten Stream-Listen sind meist die Master-Listen.
            var choice = await MediaProbe.ProbeBestAsync(_candidates.Take(6).ToList(),
                (url, token) => YtDlp.ProbeAsync(url.ToString(), _referrer, _userAgent, token),
                MediaProbe.DefaultGrace, _probe.Token, error => lastError = error);
            if (choice is not null)
            {
                _url = choice.Url;
                Show(choice.Info, MediaProbe.ChooseTitle(choice, _pageTitle));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        ShowError(lastError is null ? L.T("Auf dieser Seite wurde kein Video gefunden.") : L.T("Kein Video gefunden:") + " " + lastError);
    }

    private async void OnSetupTools(object sender, RoutedEventArgs e) => await SetupToolsAsync();

    /// <summary>yt-dlp und ffmpeg einrichten (Fortschritt im Fenster), danach wie gewohnt abfragen.</summary>
    private async Task SetupToolsAsync()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = L.T("Werkzeuge werden eingerichtet …");
        void OnProgress(object? s, ToolSetupProgress p) => Dispatcher.BeginInvoke(() => LoadingText.Text = Services.VideoTools.Describe(p));
        Services.VideoTools.Progress += OnProgress;
        try
        {
            await Services.VideoTools.SetupAsync(_settings.Proxy());
        }
        catch (Exception ex)
        {
            ShowError(L.T("Einrichtung fehlgeschlagen:") + " " + ex.Message);
            SetupPanel.Visibility = Visibility.Visible;
            return;
        }
        finally
        {
            Services.VideoTools.Progress -= OnProgress;
        }
        if (IsLoaded)
            await ProbeAsync();
    }

    private void Show(MediaInfo info, string title)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        TitleText.Text = title;
        var meta = new List<string>();
        if (info.Site is { } site)
            meta.Add(site);
        if (info.Duration is double seconds)
            meta.Add(TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
        MetaText.Text = string.Join("  ·  ", meta);
        if (Uri.TryCreate(info.Thumbnail, UriKind.Absolute, out var thumb) && thumb.Scheme == "https")
            Thumbnail.Source = new BitmapImage(thumb);

        NameBox.Text = FileNameHelper.Sanitize(title);
        Options.ItemsSource = info.Options;
        Options.SelectedItem = Preselect(info.Options);
        OptionsLabel.Visibility = Options.Visibility = Visibility.Visible;
        StartButton.IsEnabled = LaterButton.IsEnabled = true;
    }

    /// <summary>Zuletzt gewählte Qualität oder die nächstkleinere; sonst die beste.</summary>
    private MediaOption Preselect(IReadOnlyList<MediaOption> options)
    {
        if (options.FirstOrDefault(o => o.Code == _settings.PreferredMediaFormat) is { } same)
            return same;
        if (_settings.PreferredMediaFormat.StartsWith('v') && int.TryParse(_settings.PreferredMediaFormat.AsSpan(1), out int wanted))
        {
            var lower = options.Where(o => o.Code.StartsWith('v') && int.TryParse(o.Code.AsSpan(1), out int h) && h <= wanted).FirstOrDefault();
            if (lower is not null)
                return lower;
        }
        return options[0];
    }

    private void OnOptionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Audio landet ohne eigenen Ordner bei „Musik“, Video bei „Video“.
        if (Options.SelectedItem is MediaOption option && !_folderEdited)
            SetFolder(Category.For(option.IsAudio ? "x.mp3" : "x.mp4", _settings.Categories));
    }

    private void SetFolder(Category category)
    {
        _updating = true;
        FolderBox.Text = category.ResolveFolder(_settings.DownloadsFolder);
        _updating = false;
    }

    private void ShowError(string message)
    {
        LoadingPanel.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Speicherort wählen"), InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : _settings.DownloadsFolder };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnStart(object sender, RoutedEventArgs e) => Finish(true);

    private void OnLater(object sender, RoutedEventArgs e) =>
        QueuePicker.Pick((FrameworkElement)sender, Queues, queue => Finish(false, queue.Id));

    private void Finish(bool startNow, long? queueId = null)
    {
        if (_url is null || Options.SelectedItem is not MediaOption option)
            return;
        string folder = FolderBox.Text.Trim();
        string name = NameBox.Text.Trim();
        if (name.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            ErrorText.Text = L.T("Bitte einen Dateinamen und einen vollständigen Ordnerpfad angeben.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        if (!option.IsAudio)
            _settings.PreferredMediaFormat = option.Code;
        Result = new NewDownload(_url)
        {
            Kind = DownloadKind.Media,
            MediaFormat = option.Code,
            FileName = name,
            Directory = folder,
            Category = Category.For(option.IsAudio ? "x.mp3" : "x.mp4", _settings.Categories).Name,
            Referrer = _referrer,
            UserAgent = _userAgent,
            KnownSize = option.EstimatedSize ?? -1,
            Description = TitleText.Text,
            QueueId = queueId,
        };
        StartNow = startNow;
        DialogResult = true;
    }
}
