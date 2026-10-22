using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

public partial class OptionsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ObservableCollection<CategoryRow> _categories;
    private readonly AppSettings _current;

    private readonly Func<BrowserState> _browserState;
    private readonly Func<string?> _repairBrowser;

    /// <summary>Wo der Host angemeldet ist und welche Erweiterungen gerade verbunden sind.</summary>
    public sealed record BrowserState(IReadOnlyList<string> Registered, IReadOnlyList<string> Connected);

    /// <param name="repairBrowser">Meldet den Host neu an; liefert eine Fehlermeldung oder null.</param>
    public OptionsWindow(AppSettings current, Func<BrowserState> browserState, Func<string?> repairBrowser, string extensionFolder)
    {
        InitializeComponent();
        _current = current;
        _browserState = browserState;
        _repairBrowser = repairBrowser;

        // Auf einer Kopie arbeiten; erst „Speichern“ übernimmt.
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(current))!;
        StartWithWindows.IsChecked = settings.StartWithWindows;
        MinimizeToTray.IsChecked = settings.MinimizeToTray;
        MonitorClipboard.IsChecked = settings.MonitorClipboard;
        ShowFileInfo.IsChecked = settings.ShowFileInfoDialog;
        ShowComplete.IsChecked = settings.ShowCompleteDialog;
        ShowProgress.IsChecked = settings.ShowProgressDialog;
        CheckUpdates.IsChecked = settings.CheckForUpdates;
        LanguageBox.ItemsSource = L.Languages.Select(l => l.Code.Length == 0 ? L.T(l.Name) : l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, L.Languages.ToList().FindIndex(l => l.Code == settings.Language));
        ThemeBox.ItemsSource = new[] { L.T("Wie Windows"), L.T("Dunkel"), L.T("Hell") };
        ThemeBox.SelectedIndex = Math.Max(0, Theme.Modes.ToList().FindIndex(m => m.Code == settings.Theme));
        UpdateStatus.Text = L.F("Beim Start einmal am Tag auf GitHub nachsehen. Installiert: {0}", Services.Updater.CurrentVersion.ToString(3))
            + (settings.LastUpdateCheck is { } last ? ", " + L.F("zuletzt geprüft {0}", last.LocalDateTime.ToString("g", L.Culture)) : "");

        Connections.ItemsSource = new[] { 1, 2, 4, 6, 8, 12, 16, 24, 32 };
        Connections.SelectedItem = settings.MaxConnections;
        if (Connections.SelectedItem is null)
            Connections.SelectedItem = 8;
        Parallel.ItemsSource = Enumerable.Range(1, 10).ToArray();
        Parallel.SelectedItem = Math.Clamp(settings.MaxParallelDownloads, 1, 10);
        SpeedLimit.Text = settings.SpeedLimit > 0 ? ToLimitText(settings.SpeedLimit) : "0";
        IgnoreModified.IsChecked = settings.IgnoreModificationTimeChanges;
        ProxyModeBox.SelectedIndex = (int)settings.ProxyMode;
        ProxyAddressBox.Text = settings.ProxyAddress ?? "";
        ProxyUserBox.Text = settings.ProxyUser ?? "";
        // Gespeichertes Passwort wird nicht angezeigt; leer lassen = unverändert.
        _proxyPasswordProtected = settings.ProxyPasswordProtected;
        if (_proxyPasswordProtected is not null)
            ProxyPasswordBox.PlaceholderText = L.T("Passwort (gespeichert)");
        OnProxyModeChanged(this, null);

        BrowserIntegration.IsChecked = settings.BrowserIntegration;
        ShowVideoButton.IsChecked = settings.ShowVideoButton;
        _preferredMediaFormat = settings.PreferredMediaFormat;
        ToolsFolderText.Text = L.F("Werkzeuge liegen in {0}", Core.Media.MediaTools.DataToolsFolder);
        Loaded += async (_, _) => await ShowToolStatusAsync();
        ExcludedSites.Text = string.Join(" ", settings.BrowserExcludedSites);
        ExtensionFolder.Text = extensionFolder;
        FirefoxFolder.Text = Path.Combine(Path.GetDirectoryName(extensionFolder)!, "firefox");
        ShowBrowserState();

        DownloadsFolder.Text = settings.DownloadsFolder;
        _categories = new ObservableCollection<CategoryRow>(settings.Categories.Select(CategoryRow.From));
        CategoryList.ItemsSource = _categories;
    }

    public AppSettings? Result { get; private set; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        string folder = DownloadsFolder.Text.Trim();
        if (!Path.IsPathFullyQualified(folder))
        {
            ShowError(L.T("Der Download-Ordner muss ein vollständiger Pfad sein."));
            return;
        }
        if (!ByteSize.TryParse(SpeedLimit.Text, out long limit))
        {
            ShowError(L.T("Geschwindigkeitslimit nicht erkannt. Beispiele: 0, 500K, 2M."));
            return;
        }
        var names = _categories.Select(c => c.Name.Trim()).ToList();
        if (names.Any(n => n.Length == 0) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
        {
            ShowError(L.T("Jede Kategorie braucht einen eindeutigen Namen."));
            return;
        }
        if (_categories.FirstOrDefault(c => c.Folder.Trim().Length > 0 && !Path.IsPathFullyQualified(c.Folder.Trim())) is { } bad)
        {
            ShowError(L.F("Ordner der Kategorie „{0}“ muss ein vollständiger Pfad sein.", bad.Name));
            return;
        }

        var proxyMode = (ProxyMode)Math.Max(0, ProxyModeBox.SelectedIndex);
        if (proxyMode == ProxyMode.Manual && new ProxySettings(proxyMode, ProxyAddressBox.Text).ProxyUri is null)
        {
            ShowError(L.T("Die Proxy-Adresse braucht Host und Port, z. B. proxy.firma.de:8080 oder socks5://127.0.0.1:1080."));
            return;
        }

        Result = new AppSettings
        {
            ProxyMode = proxyMode,
            ProxyAddress = ProxyAddressBox.Text.Trim() is { Length: > 0 } address ? address : null,
            ProxyUser = ProxyUserBox.Text.Trim() is { Length: > 0 } user ? user : null,
            ProxyPasswordProtected = _proxyPasswordProtected,
            StartWithWindows = StartWithWindows.IsChecked == true,
            MinimizeToTray = MinimizeToTray.IsChecked == true,
            MonitorClipboard = MonitorClipboard.IsChecked == true,
            ShowFileInfoDialog = ShowFileInfo.IsChecked == true,
            ShowCompleteDialog = ShowComplete.IsChecked == true,
            ShowProgressDialog = ShowProgress.IsChecked == true,
            MaxConnections = (int)Connections.SelectedItem,
            MaxParallelDownloads = (int)Parallel.SelectedItem,
            SpeedLimit = limit,
            IgnoreModificationTimeChanges = IgnoreModified.IsChecked == true,
            DownloadsFolder = folder,
            Categories = _categories.Select(c => c.ToCategory()).ToList(),
            BrowserIntegration = BrowserIntegration.IsChecked == true,
            BrowserExcludedSites = ExcludedSites.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            ShowVideoButton = ShowVideoButton.IsChecked == true,
            PreferredMediaFormat = _preferredMediaFormat,
            CheckForUpdates = CheckUpdates.IsChecked == true,
            Language = L.Languages[Math.Max(0, LanguageBox.SelectedIndex)].Code,
            Theme = Theme.Modes[Math.Max(0, ThemeBox.SelectedIndex)].Code,
            LastUpdateCheck = _current.LastUpdateCheck,
            SkippedUpdateVersion = _current.SkippedUpdateVersion,
        };
        if (ProxyPasswordBox.Password.Length > 0)
            Result.SetProxyPassword(ProxyPasswordBox.Password);
        if (Result.ProxyUser is null)
            Result.ProxyPasswordProtected = null;
        DialogResult = true;
    }

    private string _preferredMediaFormat = "v1080";
    private string? _proxyPasswordProtected;

    private void OnProxyModeChanged(object sender, SelectionChangedEventArgs? e)
    {
        bool manual = ProxyModeBox.SelectedIndex == (int)ProxyMode.Manual;
        ProxyAddressBox.IsEnabled = manual;
        ProxyUserBox.IsEnabled = manual;
        ProxyPasswordBox.IsEnabled = manual;
    }

    private async Task ShowToolStatusAsync()
    {
        SetupToolsButton.IsEnabled = Core.Media.ToolSetup.NeedsSetup && !Services.VideoTools.IsRunning;
        FfmpegStatus.Text = Core.Media.MediaTools.FfmpegPath is { } ffmpeg
            ? L.F("Gefunden: {0}", ffmpeg)
            : L.T("Nicht gefunden. Ohne ffmpeg gibt es nur Formate mit Bild und Ton in einer Datei (meist bis 360p).");
        if (Core.Media.MediaTools.YtDlpPath is not { } ytDlp)
        {
            YtDlpStatus.Text = L.T("Nicht gefunden. „Einrichten“ lädt yt-dlp und ffmpeg.");
            UpdateYtDlpButton.IsEnabled = false;
            return;
        }
        UpdateYtDlpButton.IsEnabled = true;
        YtDlpStatus.Text = $"Version {await RunToolAsync(ytDlp, "--version")}";
    }

    private async void OnSetupTools(object sender, RoutedEventArgs e)
    {
        SetupToolsButton.IsEnabled = false;
        void OnProgress(object? s, Core.Media.ToolSetupProgress p) =>
            Dispatcher.BeginInvoke(() => FfmpegStatus.Text = Services.VideoTools.Describe(p));
        Services.VideoTools.Progress += OnProgress;
        try
        {
            await Services.VideoTools.SetupAsync(_current.Proxy());
            await ShowToolStatusAsync();
        }
        catch (Exception ex)
        {
            FfmpegStatus.Text = L.T("Einrichtung fehlgeschlagen:") + " " + ex.Message;
            SetupToolsButton.IsEnabled = true;
        }
        finally
        {
            Services.VideoTools.Progress -= OnProgress;
        }
    }

    /// <summary>yt-dlp aktualisiert sich selbst von GitHub (YouTube ändert sich häufig).</summary>
    private async void OnUpdateYtDlp(object sender, RoutedEventArgs e)
    {
        if (Core.Media.MediaTools.YtDlpPath is not { } ytDlp)
            return;
        UpdateYtDlpButton.IsEnabled = false;
        YtDlpStatus.Text = L.T("Wird aktualisiert …");
        string output = await RunToolAsync(ytDlp, "-U");
        YtDlpStatus.Text = $"{output.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}  ·  Version {await RunToolAsync(ytDlp, "--version")}";
        UpdateYtDlpButton.IsEnabled = true;
    }

    private static async Task<string> RunToolAsync(string exe, string argument)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(exe, argument)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(info)!;
            string text = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return text.Trim();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return L.T("nicht ausführbar:") + " " + ex.Message;
        }
    }

    private void ShowBrowserState()
    {
        var state = _browserState();
        BrowserStatus.Text =
            (state.Registered.Count > 0 ? L.F("Angemeldet bei: {0}.", string.Join(", ", state.Registered)) : L.T("Noch bei keinem Browser angemeldet."))
            + " "
            + (state.Connected.Count > 0
                ? L.F("Verbunden: {0}.", string.Join(", ", state.Connected))
                : L.T("Zurzeit ist keine Erweiterung verbunden."));
    }

    // Neu anmelden; die Erweiterung versucht es danach jede Minute selbst, ein Klick auf ihr rotes „!“ sofort.
    private async void OnRepairBrowser(object sender, RoutedEventArgs e)
    {
        RepairButton.IsEnabled = false;
        string? error = _repairBrowser();
        ShowBrowserState();
        RepairResult.Text = error is null
            ? L.T("Neu angemeldet. Zeigt das TUNAS-Symbol im Browser weiter ein rotes „!“, darauf klicken. Hilft das nicht, den Browser ganz schließen und neu starten.")
            : L.T("Anmelden fehlgeschlagen:") + " " + error;
        RepairResult.Visibility = Visibility.Visible;
        await Task.Delay(TimeSpan.FromSeconds(3));
        ShowBrowserState();
        RepairButton.IsEnabled = true;
    }

    private void OnOpenExtensionFolder(object sender, RoutedEventArgs e) => Services.Shell.Open(ExtensionFolder.Text);

    private void OnCopyExtensionFolder(object sender, RoutedEventArgs e) => Clipboard.SetText(ExtensionFolder.Text);

    private void OnOpenFirefoxFolder(object sender, RoutedEventArgs e) => Services.Shell.Open(FirefoxFolder.Text);

    private void OnCopyFirefoxFolder(object sender, RoutedEventArgs e) => Clipboard.SetText(FirefoxFolder.Text);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnBrowseDownloads(object sender, RoutedEventArgs e)
    {
        if (PickFolder(DownloadsFolder.Text) is { } folder)
            DownloadsFolder.Text = folder;
    }

    private void OnBrowseCategory(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CategoryRow row && PickFolder(row.Folder) is { } folder)
            row.Folder = folder;
    }

    private void OnAddCategory(object sender, RoutedEventArgs e) => _categories.Add(new CategoryRow { Name = L.T("Neu") });

    private void OnRemoveCategory(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CategoryRow { IsGeneral: false } row)
            _categories.Remove(row);
    }

    private string? PickFolder(string start)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Ordner wählen"), InitialDirectory = Directory.Exists(start) ? start : DownloadsFolder.Text };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private static string ToLimitText(long bytes) =>
        bytes % (1L << 20) == 0 ? $"{bytes >> 20}M" : bytes % 1024 == 0 ? $"{bytes >> 10}K" : bytes.ToString();

    public sealed partial class CategoryRow : ObservableObject
    {
        [ObservableProperty]
        private string _name = "";

        [ObservableProperty]
        private string _extensions = "";

        [ObservableProperty]
        private string _folder = "";

        public bool IsGeneral { get; init; }

        public static CategoryRow From(Category category) => new()
        {
            Name = category.Name,
            Extensions = string.Join(" ", category.Extensions),
            Folder = category.Folder ?? "",
            IsGeneral = category.Name == Category.General,
        };

        public Category ToCategory() => new()
        {
            Name = Name.Trim(),
            Extensions = Category.Split(Extensions),
            Folder = Folder.Trim() is { Length: > 0 } folder ? folder : null,
        };
    }
}
