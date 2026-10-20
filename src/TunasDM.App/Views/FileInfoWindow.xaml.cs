using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TunasDM.App.ViewModels;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

/// <summary>„Download-Info“: Name, Größe, Kategorie und Ziel vor dem Start.</summary>
public partial class FileInfoWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppSettings _settings;
    private readonly NewDownload _request;
    private readonly CancellationTokenSource _probe = new();
    private long _size = -1;
    private bool _folderEdited;
    private bool _updating;

    public FileInfoWindow(NewDownload request, AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        _request = request;

        UrlText.Text = Core.Secrets.MaskPassword(request.Url.ToString());
        UrlText.ToolTip = UrlText.Text;
        DescriptionBox.Text = request.Description ?? "";
        CategoryBox.ItemsSource = settings.Categories;
        SetName(request.FileName ?? DownloadItem.NameFromUrl(request.Url.ToString()));
        if (request.Directory is { Length: > 0 } directory)
        {
            FolderBox.Text = directory;
            _folderEdited = true;
        }
        FolderBox.TextChanged += (_, _) => _folderEdited |= !_updating;

        Loaded += async (_, _) => await ProbeAsync();
        Closed += (_, _) => _probe.Cancel();
    }

    public NewDownload? Result { get; private set; }
    public bool StartNow { get; private set; }

    /// <summary>Warteschlangen für „Später laden“ (bei mehreren klappt eine Auswahl auf).</summary>
    public IReadOnlyList<DownloadQueue> Queues { get; init; } = [];

    private async Task ProbeAsync()
    {
        try
        {
            var info = await Download.ProbeAsync(new DownloadRequest(_request.Url)
            {
                Referrer = _request.Referrer,
                Cookies = _request.Cookies,
                UserAgent = _request.UserAgent,
                FileName = _request.FileName,
            }, cancellationToken: _probe.Token);

            _size = info.Size;
            SizeText.Text = (info.Size >= 0 ? ByteSize.Format(info.Size) : L.T("Größe unbekannt"))
                + "  ·  " + (info.SupportsRanges ? L.T("fortsetzbar") : L.T("nicht fortsetzbar"));
            if (_request.FileName is null)
                SetName(info.FileName);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SizeText.Text = L.T("Größe unbekannt");
            ErrorText.Text = L.T("Abfrage fehlgeschlagen:") + " " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void SetName(string name)
    {
        NameBox.Text = name;
        SelectCategory(Category.For(name, _settings.Categories));
    }

    private void SelectCategory(Category category)
    {
        _updating = true;
        CategoryBox.SelectedItem = _request.Category is { } fixedName
            ? _settings.Categories.FirstOrDefault(c => c.Name == fixedName) ?? category
            : category;
        _updating = false;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_updating && IsLoaded && _request.Category is null)
            SelectCategory(Category.For(NameBox.Text, _settings.Categories));
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryBox.SelectedItem is Category category && !_folderEdited)
        {
            _updating = true;
            FolderBox.Text = category.ResolveFolder(_settings.DownloadsFolder);
            _updating = false;
        }
        IconSymbol.Symbol = new DownloadItemViewModel(new DownloadItem { Category = (CategoryBox.SelectedItem as Category)?.Name ?? Category.General }).Symbol;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Speicherort wählen"), InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : _settings.DownloadsFolder };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnStart(object sender, RoutedEventArgs e) => Finish(startNow: true);

    private void OnLater(object sender, RoutedEventArgs e) =>
        QueuePicker.Pick((FrameworkElement)sender, Queues, queue => Finish(startNow: false, queue.Id));

    private void Finish(bool startNow, long? queueId = null)
    {
        string name = NameBox.Text.Trim();
        string folder = FolderBox.Text.Trim();
        if (name.Length == 0 || folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            ErrorText.Text = L.T("Bitte einen Dateinamen und einen vollständigen Ordnerpfad angeben.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Result = _request with
        {
            FileName = name,
            Directory = folder,
            Category = (CategoryBox.SelectedItem as Category)?.Name,
            Description = DescriptionBox.Text.Trim() is { Length: > 0 } description ? description : null,
            KnownSize = _size,
            QueueId = queueId,
        };
        StartNow = startNow;
        DialogResult = true;
    }
}
