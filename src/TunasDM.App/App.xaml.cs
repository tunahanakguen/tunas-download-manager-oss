using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using TunasDM.App.Services;
using TunasDM.App.ViewModels;
using TunasDM.App.Views;
using TunasDM.Core;
using TunasDM.Core.Browser;
using TunasDM.Core.Management;
using TunasDM.Core.Media;

namespace TunasDM.App;

public partial class App : Application, IAppActions
{
    private SingleInstance? _instance;
    private DownloadManager? _manager;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private ClipboardMonitor? _clipboard;
    private BrowserBridge? _browser;
    private QueueScheduler? _scheduler;
    private DispatcherTimer? _schedulerTimer;
    private readonly Dictionary<DownloadItem, ProgressWindow> _progressWindows = new();
    private readonly HashSet<long> _quitAfter = new();
    private UpdateAddressWindow? _addressWindow;
    private bool _exiting;

    private DownloadManager Manager => _manager!;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Write("Hintergrundfehler: " + args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Logger.Write("Schwerer Fehler: " + args.ExceptionObject);
        base.OnStartup(e);

        var request = CommandLineRequest.Parse(e.Args);
        _instance = SingleInstance.TryAcquire();
        Logger.Write($"Start ({(_instance is null ? "zweite Instanz" : "erste Instanz")}): {string.Join(' ', e.Args)}");
        if (_instance is null)
        {
            // Läuft schon: Aufruf weiterreichen (ohne Argumente = Fenster zeigen).
            SingleInstance.Send(request.IsEmpty ? request with { Minimized = false } : request);
            Shutdown();
            return;
        }
        if (request.Exit)
        {
            // Nichts läuft, das beendet werden müsste.
            _instance.Dispose();
            Shutdown();
            return;
        }

        var database = new DownloadDatabase(DownloadDatabase.DefaultPath);
        _manager = new DownloadManager(database, database.LoadSettings());
        L.Use(Manager.Settings.Language);
        Theme.Apply(Manager.Settings.Theme);
        Manager.Load();
        // Die Registry ist maßgeblich: Der Installer trägt den Autostart selbst ein.
        Manager.Settings.StartWithWindows = Shell.IsStartWithWindows();
        Manager.ItemCompleted += (_, item) => Dispatcher.BeginInvoke(() => OnCompleted(item));
        Manager.ItemFailed += (_, item) => Dispatcher.BeginInvoke(() => OnFailed(item));
        Manager.QueueFinished += (_, args) => Dispatcher.BeginInvoke(() => OnQueueFinished(args));
        Manager.SyncChecked += (_, e) => Logger.Write(
            $"Sync „{e.Queue.Name}“: {e.Checked} geprüft, {e.Changed} geändert{(e.Failed > 0 ? $", {e.Failed} nicht erreichbar" : "")}.");

        // Zeitplaner: alle 15 Sekunden prüfen, ob eine Warteschlange starten oder anhalten soll.
        _scheduler = new QueueScheduler(Manager, DateTime.Now);
        _scheduler.Started += (_, queue) => Logger.Write($"Zeitplan: „{queue.Name}“ gestartet.");
        _scheduler.Stopped += (_, queue) => Logger.Write($"Zeitplan: „{queue.Name}“ angehalten.");
        _schedulerTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) => _scheduler.Tick(DateTime.Now), Dispatcher);
        _schedulerTimer.Start();

        _viewModel = new MainViewModel(Manager, this);
        _window = new MainWindow(_viewModel);
        MainWindow = _window;
        _window.Closing += OnMainWindowClosing;

        _tray = new TrayIcon(ShowMainWindow, () => AddUrl(), Manager.PauseAll, Manager.StartQueue, ShowScheduler,
            () => Fire(CheckForUpdatesAsync(manual: true)), () => _ = ExitAsync());
        _clipboard = new ClipboardMonitor(_window, () => Manager.Settings);
        _clipboard.UrlCopied += (_, url) => Dispatcher.BeginInvoke(() => Fire(OfferDownloadAsync(new NewDownload(url), fromClipboard: true)));

        _instance.Listen(r => Dispatcher.BeginInvoke(() => Handle(r)));

        _browser = new BrowserBridge(() => Manager.Settings);
        _browser.MessageReceived += (_, message) => Dispatcher.BeginInvoke(() => HandleBrowser(message));
        _browser.Start();
        RegisterNativeHost();
        ScheduleUpdateCheck();

        if (!request.Minimized)
            ShowMainWindow();
        Handle(request);
    }

    /// <summary>Aufruf von der Kommandozeile oder einer zweiten Instanz.</summary>
    private void Handle(CommandLineRequest request)
    {
        if (request.Exit)
        {
            _ = ExitAsync();
            return;
        }
        if (request.SetupTools)
            SetupToolsInBackground();
        if (request.IsEmpty)
        {
            ShowMainWindow();
            return;
        }
        if (request.Url is { } url)
        {
            var download = new NewDownload(url) { Directory = request.Directory, FileName = request.FileName };
            if (request.Silent || !Manager.Settings.ShowFileInfoDialog)
            {
                var item = Manager.Add(download, startNow: !request.AddOnly);
                if (request.QuitAfter)
                    _quitAfter.Add(item.Id);
            }
            else
            {
                Fire(OfferDownloadAsync(download, fromClipboard: false, forceQueue: request.AddOnly, quitAfter: request.QuitAfter));
            }
        }
        if (request.StartQueue)
            Manager.StartQueue();
    }

    private UpdateWindow? _updateWindow;

    /// <summary>Automatische Suche: eine halbe Minute nach dem Start, höchstens einmal am Tag, nur in der installierten Fassung.</summary>
    private void ScheduleUpdateCheck()
    {
        var settings = Manager.Settings;
        if (!settings.CheckForUpdates || !Updater.IsInstalled
            || settings.LastUpdateCheck is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(20))
            return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_exiting)
                Fire(CheckForUpdatesAsync(manual: false));
        };
        timer.Start();
    }

    /// <summary>
    /// Auf GitHub nach einer neuen Fassung suchen. Automatisch höchstens einmal am Tag und still bei Fehlern;
    /// von Hand (Infobereich) immer, mit Rückmeldung auch wenn alles aktuell ist.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateWindow is { } open)
        {
            open.Activate();
            return;
        }
        Core.Updates.UpdateInfo? update;
        try
        {
            update = await Updater.CheckAsync(Manager.Settings.Proxy());
            Manager.Settings.LastUpdateCheck = DateTimeOffset.Now;
            Manager.SaveSettings();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or Core.DownloadException or System.Text.Json.JsonException)
        {
            Logger.Write("Update-Suche: " + ex.Message);
            if (manual)
                ConfirmWindow.Inform(null, L.T("Die Update-Suche hat nicht geklappt:") + " " + ex.Message);
            return;
        }
        Logger.Write($"Update-Suche: {(update is null ? "aktuell" : $"{update.Version.ToString(3)} verfügbar")}.");
        if (update is null)
        {
            if (manual)
                ConfirmWindow.Inform(null, L.F("TUNAS Download Manager {0} ist aktuell.", Updater.CurrentVersion.ToString(3)));
            return;
        }
        if (!manual && Manager.Settings.SkippedUpdateVersion == update.Version.ToString(3))
            return;

        _updateWindow = new UpdateWindow(update, () => Manager.Settings.Proxy());
        string? setup = null;
        try
        {
            if (_updateWindow.ShowDialog() == true)
                setup = _updateWindow.SetupPath;
            if (_updateWindow.Skipped)
            {
                Manager.Settings.SkippedUpdateVersion = update.Version.ToString(3);
                Manager.SaveSettings();
            }
        }
        finally
        {
            _updateWindow = null;
        }
        if (setup is not null)
        {
            Logger.Write($"Update auf {update.Version.ToString(3)} wird installiert.");
            // Erst sauber beenden (Downloads halten an), dann ersetzt das Setup die Dateien und startet TUNAS neu.
            await ExitAsync(() => Updater.RunSetup(setup));
        }
    }

    /// <summary>Vom Installer angestoßen: yt-dlp und ffmpeg laden, Ergebnis als Hinweis im Infobereich.</summary>
    private async void SetupToolsInBackground()
    {
        if (!ToolSetup.NeedsSetup)
            return;
        try
        {
            await VideoTools.SetupAsync(Manager.Settings.Proxy());
            _tray?.ShowBalloon("TUNAS Download Manager", L.T("Video-Werkzeuge (yt-dlp, ffmpeg) sind eingerichtet."));
        }
        catch (Exception ex)
        {
            _tray?.ShowBalloon(L.T("Video-Werkzeuge nicht eingerichtet"), ex.Message);
        }
    }

    /// <summary>Ordner der mitgelieferten Browser-Erweiterung (zum Laden als „entpackte Erweiterung“).</summary>
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "browser", "extension");

    private static string HostPath => Path.Combine(AppContext.BaseDirectory, "TunasDM.Host.exe");

    /// <summary>
    /// Host bei Chrome, Edge und Firefox anmelden (bei jedem Start, falls das Programm verschoben wurde,
    /// und über „Reparieren“ in den Optionen). Liefert eine Fehlermeldung oder null.
    /// </summary>
    private static string? RegisterNativeHost()
    {
        if (!File.Exists(HostPath))
        {
            Logger.Write("TunasDM.Host.exe fehlt, Browser-Integration nicht angemeldet.");
            return L.T("TunasDM.Host.exe fehlt im Programmordner. Bitte den TUNAS Download Manager neu installieren.");
        }
        try
        {
            string host = NativeHostRegistration.InstallHost(AppContext.BaseDirectory, Path.Combine(Logger.Folder, "host"));
            NativeHostRegistration.Register(host, Logger.Folder);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Write("Anmeldung bei den Browsern fehlgeschlagen: " + ex.Message);
            return ex.Message;
        }
    }

    private void HandleBrowser(BrowserMessage message)
    {
        switch (message.Type)
        {
            case "download" when message.ToNewDownload() is { } download:
                // „Adresse aktualisieren“ wartet auf den neuen Link: dorthin statt als neuer Download.
                if (_addressWindow is { } waiting)
                    waiting.TakeFromBrowser(download);
                else
                    Fire(OfferDownloadAsync(download, fromClipboard: false, fromBrowser: true));
                break;
            case "media" when message.MediaCandidates() is { Count: > 0 } candidates:
                Fire(OfferMediaAsync(candidates, message.Title, message.Referrer ?? message.PageUrl, message.UserAgent));
                break;
            case "links" when message.Links is { Count: > 0 } links:
                var dialog = new LinksWindow(links, message.Referrer, Manager.Settings);
                if (dialog.ShowDialog() == true)
                {
                    // Keine Cookies: die Links können auf beliebige andere Server zeigen.
                    foreach (var url in dialog.Selected)
                        Manager.Add(new NewDownload(url) { Referrer = message.Referrer, UserAgent = message.UserAgent }, startNow: false);
                    if (dialog.StartNow)
                        Manager.StartQueue();
                }
                break;
            // Kontextmenü „Seite mit TUNAS durchsuchen“: Grabber mit der Seite des Tabs.
            case "grab" when Uri.TryCreate(message.Url, UriKind.Absolute, out var page) && page.Scheme is "http" or "https":
                ShowGrabber(page.ToString());
                break;
        }
    }

    /// <summary>Site-Grabber: Website durchsuchen, gefundene Dateien auswählen und in die Warteschlange stellen.</summary>
    public void ShowGrabber() => ShowGrabber(null);

    /// <param name="startUrl">Vorgabe für die Startadresse; ohne sie die Webadresse aus der Zwischenablage, falls vorhanden.</param>
    public void ShowGrabber(string? startUrl)
    {
        ShowMainWindow();
        // Steht eine Webadresse in der Zwischenablage, ist sie vermutlich gemeint.
        string? clip = startUrl;
        try
        {
            if (clip is null && Clipboard.ContainsText() && Clipboard.GetText().Trim() is { Length: < 2048 } text
                && Uri.TryCreate(text, UriKind.Absolute, out var copied) && copied.Scheme is "http" or "https")
                clip = copied.ToString();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Zwischenablage gerade belegt.
        }

        var grabber = new GrabberWindow(clip, () => Manager.Settings.Proxy()) { Owner = _window };
        if (grabber.ShowDialog() != true || grabber.StartUrl is not { } start)
            return;
        var found = grabber.Files;
        var dialog = new LinksWindow(found.Select(f => new LinkInfo(f.Url.ToString(), f.Text)), start.ToString(), Manager.Settings, selectAll: true)
        {
            Owner = _window,
            Title = L.F("Site-Grabber – {0} Dateien", found.Count),
        };
        if (dialog.ShowDialog() != true)
            return;
        var byUrl = found.GroupBy(f => f.Url.ToString()).ToDictionary(g => g.Key, g => g.First());
        int added = 0;
        foreach (var url in dialog.Selected)
        {
            if (Manager.FindDuplicate(url) is not null)
                continue;
            // Referer = Seite, auf der die Datei verlinkt war (manche Server prüfen das).
            Manager.Add(new NewDownload(url) { Referrer = byUrl.GetValueOrDefault(url.ToString())?.Page.ToString() }, startNow: false);
            added++;
        }
        Logger.Write($"Site-Grabber {start.Host}: {found.Count} gefunden, {added} hinzugefügt.");
        if (dialog.StartNow && added > 0)
            Manager.StartQueue();
    }

    /// <summary>Link-Liste (.txt, IDM .ef2, .html) einlesen und die Links zur Auswahl zeigen.</summary>
    public void ImportList(string? path = null)
    {
        ShowMainWindow();
        if (path is null)
        {
            var open = new Microsoft.Win32.OpenFileDialog { Title = L.T("Link-Liste importieren"), Filter = LinkList.FileFilter };
            if (open.ShowDialog(_window) != true)
                return;
            path = open.FileName;
        }

        IReadOnlyList<ImportedLink> links;
        try
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new IOException(L.T("Die Datei ist größer als 32 MB."));
            links = LinkList.Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfirmWindow.Inform(_window, L.T("Die Liste konnte nicht gelesen werden:") + " " + ex.Message);
            return;
        }
        if (links.Count == 0)
        {
            ConfirmWindow.Inform(_window, L.F("In „{0}“ wurden keine Links gefunden.", Path.GetFileName(path)));
            return;
        }

        // HTML-Seiten enthalten viel Beiwerk: dort wie bei „Alle Links“ nach Dateitypen vorauswählen.
        bool html = Path.GetExtension(path).ToLowerInvariant() is ".htm" or ".html";
        var dialog = new LinksWindow(links.Select(l => new LinkInfo(l.Url.ToString(), l.Text)), path, Manager.Settings, selectAll: !html)
        {
            Owner = _window,
            Title = L.T("Link-Liste importieren"),
        };
        if (dialog.ShowDialog() != true)
            return;
        var byUrl = links.ToDictionary(l => l.Url.ToString());
        int added = 0;
        foreach (var url in dialog.Selected)
        {
            var link = byUrl.GetValueOrDefault(url.ToString());
            if (Manager.FindDuplicate(url) is not null)
                continue; // steht schon in der Liste
            Manager.Add(new NewDownload(url) { Referrer = link?.Referrer, UserAgent = link?.UserAgent }, startNow: false);
            added++;
        }
        Logger.Write($"Import „{Path.GetFileName(path)}“: {added} von {dialog.Selected.Count} hinzugefügt.");
        if (added < dialog.Selected.Count)
            ConfirmWindow.Inform(_window, L.F("{0} Links hinzugefügt, {1} standen schon in der Liste.", added, dialog.Selected.Count - added));
        if (dialog.StartNow && added > 0)
            Manager.StartQueue();
    }

    /// <summary>Download-Liste als IDM-Liste (.ef2) oder Textdatei speichern – ohne Cookies.</summary>
    public void ExportList(IReadOnlyList<DownloadItem> items)
    {
        if (items.Count == 0)
        {
            ConfirmWindow.Inform(_window, L.T("Die Liste ist leer."));
            return;
        }
        var save = new Microsoft.Win32.SaveFileDialog
        {
            Title = items.Count == Manager.Items.Count ? L.T("Download-Liste exportieren") : L.F("{0} markierte Downloads exportieren", items.Count),
            Filter = L.T("IDM-Liste (*.ef2)|*.ef2|Textdatei, ein Link je Zeile (*.txt)|*.txt"),
            FileName = $"TUNAS-Liste-{DateTime.Now:yyyy-MM-dd}.ef2",
        };
        if (save.ShowDialog(_window) != true)
            return;
        try
        {
            File.WriteAllText(save.FileName, LinkList.Write(items, save.FileName), new System.Text.UTF8Encoding(false));
            Logger.Write($"Export: {items.Count} Einträge nach {save.FileName}.");
            _tray?.ShowBalloon(L.T("Liste exportiert"), L.F("{0} Einträge in {1}", items.Count, Path.GetFileName(save.FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConfirmWindow.Inform(_window, L.T("Die Liste konnte nicht gespeichert werden:") + " " + ex.Message);
        }
    }

    public void AddUrl(string? url = null)
    {
        ShowMainWindow();
        var dialog = new AddUrlWindow(url) { Owner = _window };
        if (dialog.ShowDialog() != true)
            return;
        if (dialog.BatchUrls.Count > 0)
            AddBatch(dialog.BatchUrls);
        else if (dialog.Url is { } uri)
            Fire(OfferDownloadAsync(new NewDownload(uri), fromClipboard: false));
    }

    /// <summary>Batch-Download: alle erzeugten Adressen zur Auswahl, dann in die Warteschlange.</summary>
    private void AddBatch(IReadOnlyList<Uri> urls)
    {
        var dialog = new LinksWindow(urls.Select(u => new LinkInfo(u.ToString(), null)), L.F("{0} Adressen aus dem Muster", urls.Count),
            Manager.Settings, selectAll: true)
        {
            Owner = _window,
            Title = L.T("Batch-Download"),
        };
        if (dialog.ShowDialog() != true)
            return;
        int added = 0;
        foreach (var link in dialog.Selected)
        {
            if (Manager.FindDuplicate(link) is not null)
                continue;
            Manager.Add(new NewDownload(link), startNow: false);
            added++;
        }
        Logger.Write($"Batch-Download: {added} von {dialog.Selected.Count} hinzugefügt.");
        if (dialog.StartNow && added > 0)
            Manager.StartQueue();
    }

    /// <summary>„Video laden“: Qualität wählen, dann als Medien-Download (yt-dlp) einreihen.</summary>
    private async Task OfferMediaAsync(IReadOnlyList<Uri> candidates, string? title, string? referrer, string? userAgent)
    {
        var dialog = new MediaWindow(candidates, title, referrer, userAgent, Manager.Settings) { Queues = Manager.Queues };
        if (dialog.ShowDialog() == true && dialog.Result is { } result && await ResolveDuplicateAsync(result))
        {
            var item = Manager.Add(result, startNow: dialog.StartNow);
            Manager.ApplySettings(Manager.Settings); // gewählte Qualität merken
            ShowProgressOnStart(item, dialog.StartNow);
        }
    }

    /// <summary>
    /// Steht dieselbe Adresse schon in der Liste, fragen. True = neuen Eintrag anlegen;
    /// false = erledigt (vorhandenen fortgesetzt oder gezeigt) oder abgebrochen.
    /// </summary>
    private async Task<bool> ResolveDuplicateAsync(NewDownload request)
    {
        // Bei Videos ist eine andere Qualität (z. B. nur Ton) kein Duplikat.
        if (Manager.FindDuplicate(request.Url) is not { } existing
            || (request.Kind == DownloadKind.Media && existing.MediaFormat != request.MediaFormat))
            return true;

        var dialog = new DuplicateWindow(existing);
        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case DuplicateChoice.UseExisting when existing.Status == DownloadStatus.Completed:
                if (existing.FilePath is { } path)
                    Shell.ShowInFolder(path);
                return false;
            case DuplicateChoice.UseExisting:
                Manager.Start(existing);
                ShowProgress(existing);
                return false;
            case DuplicateChoice.AddNew:
                return true;
            case DuplicateChoice.Replace:
                await Manager.RemoveAsync(existing, deleteFile: true);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Datei-Info-Dialog zeigen (oder direkt starten, wenn abgeschaltet).</summary>
    private async Task OfferDownloadAsync(NewDownload download, bool fromClipboard, bool forceQueue = false, bool quitAfter = false,
        bool fromBrowser = false)
    {
        // Video-Seiten (YouTube & Co.) und Stream-Listen gehen an yt-dlp statt an die Datei-Engine.
        if (download.Kind == DownloadKind.File && Core.Media.MediaSites.IsMediaPage(download.Url))
        {
            await OfferMediaAsync([download.Url], null, download.Referrer, download.UserAgent);
            return;
        }
        if (!await ResolveDuplicateAsync(download))
            return;
        if (!Manager.Settings.ShowFileInfoDialog && !fromClipboard)
        {
            ShowProgressOnStart(Manager.Add(download, startNow: !forceQueue), !forceQueue);
            return;
        }

        var dialog = new FileInfoWindow(download, Manager.Settings) { Queues = Manager.Queues };
        if (_window is { IsVisible: true, IsActive: true })
            dialog.Owner = _window;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Aus Browser oder Zwischenablage: über dem Browser erscheinen, nicht hinter ihm.
        dialog.Topmost = fromClipboard || fromBrowser;
        dialog.ShowActivated = true;

        if (dialog.ShowDialog() == true && dialog.Result is { } result)
        {
            var item = Manager.Add(result, startNow: dialog.StartNow && !forceQueue);
            if (quitAfter)
                _quitAfter.Add(item.Id);
            ShowProgressOnStart(item, dialog.StartNow && !forceQueue);
        }
    }

    /// <summary>Ein gerade gestarteter Download bekommt sofort sein eigenes Fortschrittsfenster.</summary>
    private void ShowProgressOnStart(DownloadItem item, bool started)
    {
        if (started && Manager.Settings.ShowProgressDialog)
            ShowProgress(item);
    }

    public void ShowOptions()
    {
        var dialog = new OptionsWindow(Manager.Settings,
            () => new OptionsWindow.BrowserState(NativeHostRegistration.RegisteredBrowsers(), _browser?.ConnectedBrowsers ?? []),
            RegisterNativeHost, ExtensionFolder) { Owner = _window };
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings)
            return;

        try
        {
            if (settings.StartWithWindows != Manager.Settings.StartWithWindows)
                Shell.SetStartWithWindows(settings.StartWithWindows);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ConfirmWindow.Inform(_window, L.T("Autostart konnte nicht geändert werden:") + " " + ex.Message);
            settings.StartWithWindows = Manager.Settings.StartWithWindows;
        }
        bool languageChanged = settings.Language != Manager.Settings.Language || settings.Theme != Manager.Settings.Theme;
        Manager.ApplySettings(settings);
        _viewModel!.RebuildFilters();
        _browser?.PushConfig();

        // Die Sprache steckt in allen Fenstern: Neustart statt halb übersetzter Oberfläche.
        if (languageChanged && ConfirmWindow.Ask(_window, L.T("Sprache und Design wechseln nach einem Neustart von TUNAS. Jetzt neu starten?"),
                L.T("Neu starten"), L.T("Später")))
        {
            string exe = Environment.ProcessPath!;
            _ = ExitAsync(() => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }));
        }
    }

    /// <summary>Asynchronen Ablauf vom UI-Thread starten; Fehler landen im allgemeinen Fehlerdialog.</summary>
    private static async void Fire(Task task) => await task;

    /// <summary>„Adresse aktualisieren“ für einen abgelaufenen Link; danach weiterladen.</summary>
    public void UpdateAddress(DownloadItem item)
    {
        if (_addressWindow is { } open)
        {
            open.Activate();
            return;
        }
        if (item.IsActive)
            Manager.Pause(item);

        var dialog = new UpdateAddressWindow(item) { Owner = _window };
        _addressWindow = dialog;
        bool ok;
        try
        {
            ok = dialog.ShowDialog() == true;
        }
        finally
        {
            _addressWindow = null;
        }
        if (!ok || dialog.Result is not { } result)
            return;

        try
        {
            Manager.UpdateAddress(item, result.Url, result.Cookies, result.Referrer, result.UserAgent);
        }
        catch (InvalidOperationException)
        {
            ConfirmWindow.Inform(_window, L.T("Der Download läuft noch. Bitte kurz warten, bis er pausiert ist, und es dann erneut versuchen."));
            return;
        }
        Manager.Start(item);
        ShowProgressOnStart(item, started: true);
    }

    public void ShowProgress(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item, out var open))
        {
            open.Activate();
            return;
        }
        var window = new ProgressWindow(item, Manager);
        // Bei verstecktem Hauptfenster (Download aus dem Browser) frei auf dem Bildschirm, sonst über dem Hauptfenster.
        if (_window is { IsVisible: true, WindowState: not WindowState.Minimized })
            window.Owner = _window;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Closed += (_, _) => _progressWindows.Remove(item);
        _progressWindows[item] = window;
        window.Show();
        window.Activate();
    }

    public bool Confirm(string message) => ConfirmWindow.Ask(_window, message);

    private void OnCompleted(DownloadItem item)
    {
        if (_progressWindows.TryGetValue(item, out var progress))
            progress.Close();

        if (_quitAfter.Remove(item.Id) && !Manager.Items.Any(i => i.IsActive))
        {
            _ = ExitAsync();
            return;
        }

        // Aus einer Warteschlange (oft nachts, viele Dateien): kein Fenster je Datei; die Warteschlange meldet sich am Ende.
        if (item.StartedByQueue)
            return;

        if (Manager.Settings.ShowCompleteDialog)
        {
            var dialog = new CompleteWindow(item);
            dialog.Closed += (_, _) =>
            {
                if (dialog.DontShowAgain)
                {
                    Manager.Settings.ShowCompleteDialog = false;
                    Manager.ApplySettings(Manager.Settings);
                }
            };
            dialog.Show();
        }
        else if (_window is not { IsVisible: true })
        {
            _tray?.ShowBalloon(L.T("Download abgeschlossen"), item.DisplayName);
        }
    }

    private void OnFailed(DownloadItem item)
    {
        _quitAfter.Remove(item.Id);
        if (_window is not { IsVisible: true })
            _tray?.ShowBalloon(L.T("Download fehlgeschlagen"), $"{item.DisplayName}: {item.Error}");
    }

    private void ShowMainWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting)
            return;
        e.Cancel = true;
        if (Manager.Settings.MinimizeToTray)
            _window!.Hide();
        else
            _ = ExitAsync();
    }

    /// <summary>Alle Downloads pausieren (Stände sichern), dann beenden; <paramref name="then"/> läuft danach (z. B. Herunterfahren).</summary>
    private async Task ExitAsync(Action? then = null)
    {
        if (_exiting)
            return;
        _exiting = true;
        _schedulerTimer?.Stop();
        _tray?.SetToolTip(L.T("TUNAS Download Manager wird beendet …"));
        try
        {
            await Manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Logger.Write("Beim Beenden: " + ex);
        }
        _clipboard?.Dispose();
        _browser?.Dispose();
        _tray?.Dispose();
        _instance?.Dispose();
        _window?.Close();
        then?.Invoke();
        Shutdown();
    }

    /// <summary>Zeitplaner (Warteschlangen) öffnen.</summary>
    public void ShowScheduler()
    {
        ShowMainWindow();
        new SchedulerWindow(Manager) { Owner = _window }.ShowDialog();
        _viewModel!.RebuildFilters();
    }

    /// <summary>Warteschlange von selbst fertig: mit einer Minute Countdown die Aktion danach ausführen.</summary>
    private async void OnQueueFinished(QueueFinishedEventArgs e)
    {
        var queue = e.Queue;
        if (!e.Downloaded || _exiting)
            return;
        if (queue.After == AfterQueueAction.RunProgram)
        {
            // Ein Programm zu starten schadet nicht: ohne Countdown.
            RunAfterProgram(queue);
            return;
        }
        if (queue.After == AfterQueueAction.None)
        {
            if (_window is not { IsVisible: true })
                _tray?.ShowBalloon(L.T("Warteschlange fertig"), queue.Name);
            return;
        }
        // Läuft noch etwas anderes (nicht aus dieser Warteschlange), trotzdem fragen: der Countdown lässt Zeit zum Abbrechen.
        var countdown = new CountdownWindow(queue);
        countdown.ShowDialog();
        if (!countdown.Proceed)
            return;
        Logger.Write($"Warteschlange „{queue.Name}“ fertig, Aktion: {queue.After}.");
        switch (queue.After)
        {
            case AfterQueueAction.ExitApp:
                await ExitAsync();
                break;
            case AfterQueueAction.Shutdown:
                await ExitAsync(PowerActions.Shutdown);
                break;
            case AfterQueueAction.Sleep:
                PowerActions.Sleep();
                break;
            case AfterQueueAction.Hibernate:
                PowerActions.Hibernate();
                break;
        }
    }

    private void RunAfterProgram(DownloadQueue queue)
    {
        if (string.IsNullOrWhiteSpace(queue.AfterProgram))
            return;
        try
        {
            var start = new ProcessStartInfo(queue.AfterProgram, queue.AfterArguments ?? "")
            {
                UseShellExecute = true, // auch .bat, .ps1 oder Dokumente
                WorkingDirectory = Path.GetDirectoryName(queue.AfterProgram) is { Length: > 0 } folder && Directory.Exists(folder) ? folder : "",
            };
            Process.Start(start);
            Logger.Write($"Warteschlange „{queue.Name}“ fertig, gestartet: {queue.AfterProgram} {queue.AfterArguments}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Logger.Write($"Warteschlange „{queue.Name}“: Programm nicht gestartet: {ex.Message}");
            _tray?.ShowBalloon(L.T("Programm nicht gestartet"), $"{Path.GetFileName(queue.AfterProgram)}: {ex.Message}");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Write("Unerwarteter Fehler: " + e.Exception);
        ConfirmWindow.Inform(_window, L.T("Es ist ein unerwarteter Fehler aufgetreten:") + "\n\n" + e.Exception.Message +
            "\n\n" + L.F("Das Programm läuft weiter. Details stehen im Protokoll unter {0}.", Logger.Folder));
        e.Handled = true;
    }
}
