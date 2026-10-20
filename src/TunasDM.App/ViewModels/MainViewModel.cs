using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunasDM.App.Services;
using TunasDM.Core;
using TunasDM.Core.Management;
using Wpf.Ui.Controls;

namespace TunasDM.App.ViewModels;

/// <summary>Was das Hauptfenster von der App braucht (Dialoge, Rückfragen).</summary>
public interface IAppActions
{
    void AddUrl(string? url = null);
    void ShowOptions();
    void ShowProgress(DownloadItem item);
    void UpdateAddress(DownloadItem item);
    void ShowScheduler();
    void ImportList(string? path = null);
    void ShowGrabber();
    void ExportList(IReadOnlyList<DownloadItem> items);
    bool Confirm(string message);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly IAppActions _actions;
    private readonly Dictionary<DownloadItem, DownloadItemViewModel> _byItem = new();
    private readonly DispatcherTimer _timer;
    private string _sortProperty = nameof(DownloadItemViewModel.AddedAt);
    private ListSortDirection _sortDirection = ListSortDirection.Descending;

    public MainViewModel(DownloadManager manager, IAppActions actions)
    {
        _manager = manager;
        _actions = actions;

        foreach (var item in manager.Items)
            AddRow(item);
        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => o is DownloadItemViewModel row && (SelectedFilter?.Match(row.Item) ?? true) && MatchesSearch(row);
        ApplySort();
        RebuildFilters();

        manager.ItemAdded += (_, item) => OnUi(() => AddRow(item));
        manager.ItemRemoved += (_, item) => OnUi(() => RemoveRow(item));
        manager.QueueStateChanged += (_, _) => OnUi(() =>
        {
            OnPropertyChanged(nameof(QueueRunning));
            StartQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            // Name, Zeitplan oder Anzahl der Warteschlangen können sich geändert haben.
            // (Vergleich über eine Momentaufnahme: die Objekte selbst sind dieselben wie in QueueChoices.)
            if (QueueSignature() != _queueSignature)
                RebuildFilters();
            UpdateQueueStatus();
        });

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(),
            Application.Current.Dispatcher);
        _timer.Start();
        Refresh();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    /// <summary>Baum links: Alle / Unfertig / Fertig (je mit Kategorien) und Warteschlange.</summary>
    public ObservableCollection<FilterViewModel> Filters { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value) => ItemsView.Refresh();

    private bool MatchesSearch(DownloadItemViewModel row) =>
        SearchText.Trim() is not { Length: > 0 } search
        || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
        || row.Url.Contains(search, StringComparison.OrdinalIgnoreCase);

    public string SortProperty => _sortProperty;
    public bool SortAscending => _sortDirection == ListSortDirection.Ascending;

    /// <summary>Vom Code-Behind gesetzt (Mehrfachauswahl der Liste).</summary>
    public IReadOnlyList<DownloadItemViewModel> SelectedItems { get; private set; } = [];

    [ObservableProperty]
    private FilterViewModel? _selectedFilter;

    [ObservableProperty]
    private DownloadItemViewModel? _selectedItem;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _speedText = "";

    [ObservableProperty]
    private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;

    [ObservableProperty]
    private double _taskbarProgress;

    public bool QueueRunning => _manager.QueueRunning;

    private string _queueSignature = "";

    /// <summary>Was der Baum von den Warteschlangen zeigt; ändert es sich, wird er neu aufgebaut.</summary>
    private string QueueSignature() =>
        string.Join("|", _manager.Queues.Select(q => $"{q.Id}:{q.Name}:{q.Schedule.StartEnabled}:{q.Sync}"));

    /// <summary>Für „In Warteschlange ▸“ im Kontextmenü.</summary>
    public ObservableCollection<DownloadQueue> QueueChoices { get; } = new();

    /// <summary>Statuszeile rechts: was läuft, sonst der nächste geplante Start.</summary>
    [ObservableProperty]
    private string _queueStatusText = "";

    private void UpdateQueueStatus()
    {
        var queues = _manager.Queues;
        var running = queues.Where(_manager.IsRunning).Select(q => q.DisplayName).ToList();
        if (running.Count > 0)
        {
            QueueStatusText = L.F(running.Count == 1 ? "Läuft: {0}" : "Laufen: {0}", string.Join(", ", running));
            return;
        }
        var now = DateTime.Now;
        var next = queues.Select(q => (Name: q.DisplayName, At: q.Schedule.NextStart(now))).Where(x => x.At is not null).OrderBy(x => x.At).FirstOrDefault();
        QueueStatusText = next.At is { } at
            ? L.F("Nächster Start: {0}, {1} {2}", next.Name,
                at.Date == now.Date ? L.T("heute") : at.Date == now.Date.AddDays(1) ? L.T("morgen") : at.ToString("ddd d.M.", L.Culture),
                at.ToString("t", L.Culture))
            : L.T("Warteschlangen angehalten");
    }

    public bool HasItems => Items.Count > 0;

    public void SetSelection(IReadOnlyList<DownloadItemViewModel> rows)
    {
        SelectedItems = rows;
        NotifyCommands();
    }

    partial void OnSelectedFilterChanged(FilterViewModel? value)
    {
        ItemsView.Refresh();
        StartQueueCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Spaltenkopf geklickt: nach dieser Eigenschaft sortieren, erneut = umkehren.</summary>
    [RelayCommand]
    public void SortBy(string property)
    {
        _sortDirection = _sortProperty == property && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending : ListSortDirection.Ascending;
        _sortProperty = property;
        ApplySort();
        OnPropertyChanged(nameof(SortProperty));
        OnPropertyChanged(nameof(SortAscending));
    }

    private void ApplySort()
    {
        ItemsView.SortDescriptions.Clear();
        ItemsView.SortDescriptions.Add(new SortDescription(_sortProperty, _sortDirection));
    }

    public void RebuildFilters()
    {
        string selected = SelectedFilter?.Path ?? "Alle";
        Filters.Clear();
        Filters.Add(Branch("Alle", L.T("Alle Downloads"), SymbolRegular.ArrowDownload24, _ => true, expanded: true));
        Filters.Add(Branch("Unfertig", L.T("Unfertig"), SymbolRegular.Clock24, i => i.Status != DownloadStatus.Completed));
        Filters.Add(Branch("Fertig", L.T("Fertig"), SymbolRegular.CheckmarkCircle24, i => i.Status == DownloadStatus.Completed));
        // Jede Warteschlange mit ihren unfertigen Downloads.
        var queues = _manager.Queues;
        var queueBranch = new FilterViewModel(L.T("Warteschlangen"), SymbolRegular.TextBulletListLtr24, i => i.Status == DownloadStatus.Queued)
        {
            Path = "Warteschlange", IsExpanded = queues.Count > 1,
        };
        foreach (var queue in queues)
        {
            long id = queue.Id;
            bool sync = queue.Sync; // Sync-Warteschlangen zeigen auch ihre fertigen Dateien.
            queueBranch.Children.Add(new FilterViewModel(queue.DisplayName, queue.Sync ? SymbolRegular.ArrowSync24 : queue.Schedule.StartEnabled ? SymbolRegular.CalendarClock24 : SymbolRegular.TextBulletListLtr24,
                i => i.QueueId == id && (sync || i.Status != DownloadStatus.Completed)) { Path = $"Warteschlange/{id}" });
        }
        Filters.Add(queueBranch);

        QueueChoices.Clear();
        foreach (var queue in queues)
            QueueChoices.Add(queue);
        _queueSignature = QueueSignature();

        var all = Filters.SelectMany(f => f.SelfAndDescendants()).ToList();
        SelectFilter(all.FirstOrDefault(f => f.Path == selected) ?? Filters[0]);
        UpdateCounts();
    }

    /// <summary>Ein Zustand mit allen Kategorien darunter.</summary>
    private FilterViewModel Branch(string path, string name, SymbolRegular symbol, Func<DownloadItem, bool> match, bool expanded = false)
    {
        var branch = new FilterViewModel(name, symbol, match) { Path = path, IsExpanded = expanded };
        foreach (var category in _manager.Settings.Categories)
        {
            string categoryName = category.Name;
            branch.Children.Add(new FilterViewModel(L.T(categoryName), FilterViewModel.SymbolFor(categoryName),
                i => match(i) && i.Category == categoryName) { Path = $"{path}/{categoryName}" });
        }
        return branch;
    }

    public void SelectFilter(FilterViewModel filter)
    {
        foreach (var f in Filters.SelectMany(f => f.SelfAndDescendants()))
            f.IsSelected = f == filter;
        SelectedFilter = filter;
    }

    private void AddRow(DownloadItem item)
    {
        if (_byItem.ContainsKey(item))
            return;
        var row = new DownloadItemViewModel(item);
        _byItem[item] = row;
        Items.Add(row);
        OnPropertyChanged(nameof(HasItems));
        UpdateCounts();
    }

    private void RemoveRow(DownloadItem item)
    {
        if (!_byItem.Remove(item, out var row))
            return;
        Items.Remove(row);
        if (SelectedItem == row)
            SelectedItem = null;
        OnPropertyChanged(nameof(HasItems));
        UpdateCounts();
    }

    private void Refresh()
    {
        bool anyStatusChange = false;
        foreach (var row in Items)
        {
            var before = row.Status;
            row.Refresh();
            anyStatusChange |= before != row.Status;
        }
        if (anyStatusChange)
        {
            ItemsView.Refresh();
            UpdateCounts();
        }

        var active = Items.Where(r => r.IsActive).ToList();
        double speed = active.Sum(r => r.Speed);
        long total = active.Sum(r => Math.Max(0, r.Progress.TotalSize));
        long done = active.Sum(r => r.Progress.Downloaded);

        SpeedText = active.Count > 0 ? $"{ByteSize.Format(speed)}/s" : "";
        string limit = _manager.Limiter.BytesPerSecond > 0 ? "  ·  " + L.F("Limit {0}/s", ByteSize.Format(_manager.Limiter.BytesPerSecond)) : "";
        StatusText = active.Count switch
        {
            0 => L.F(Items.Count == 1 ? "{0} Download" : "{0} Downloads", Items.Count) + limit,
            1 => L.T("1 aktiver Download") + limit,
            _ => L.F("{0} aktive Downloads", active.Count) + limit,
        };
        TaskbarState = active.Count == 0 ? TaskbarItemProgressState.None
            : total > 0 ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
        TaskbarProgress = total > 0 ? done / (double)total : 0;
        UpdateQueueStatus();
        NotifyCommands();
    }

    private void UpdateCounts()
    {
        foreach (var filter in Filters.SelectMany(f => f.SelfAndDescendants()))
            filter.Count = Items.Count(r => filter.Match(r.Item));
    }

    // Aktionen direkt an der Zeile (erscheinen beim Überfahren).

    [RelayCommand]
    private void ToggleRow(DownloadItemViewModel? row)
    {
        if (row is null)
            return;
        if (row.IsActive || row.Status == DownloadStatus.Queued)
            _manager.Pause(row.Item);
        else if (row.Item.CanStart)
            _manager.Start(row.Item);
        else if (row.IsCompleted)
            Activate(row);
        row.Refresh();
    }

    [RelayCommand]
    private void OpenFolderRow(DownloadItemViewModel? row)
    {
        if (row is null)
            return;
        if (row.FilePath is { } path && File.Exists(path))
            Shell.ShowInFolder(path);
        else
            Shell.Open(row.Item.Directory);
    }

    private IEnumerable<DownloadItemViewModel> Targets =>
        SelectedItems.Count > 0 ? SelectedItems : SelectedItem is { } one ? [one] : [];

    private void NotifyCommands()
    {
        ResumeCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        DeleteWithFileCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        EnqueueCommand.NotifyCanExecuteChanged();
        OpenFileCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        CopyUrlCommand.NotifyCanExecuteChanged();
        UpdateAddressCommand.NotifyCanExecuteChanged();
        StartQueueCommand.NotifyCanExecuteChanged();
        StopQueueCommand.NotifyCanExecuteChanged();
        PauseAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddUrl() => _actions.AddUrl();

    public void AddUrlFromDrop(string url) => _actions.AddUrl(url);

    public void ImportFromDrop(string path) => _actions.ImportList(path);

    [RelayCommand]
    private void Options() => _actions.ShowOptions();

    private bool CanResume() => Targets.Any(r => r.Item.CanStart);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        foreach (var row in Targets.Where(r => r.Item.CanStart).ToList())
            _manager.Start(row.Item);
    }

    private bool CanPause() => Targets.Any(r => r.IsActive || r.Status == DownloadStatus.Queued);

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        foreach (var row in Targets.ToList())
            _manager.Pause(row.Item);
    }

    private bool CanPauseAll() => Items.Any(r => r.IsActive || r.Status == DownloadStatus.Queued);

    [RelayCommand(CanExecute = nameof(CanPauseAll))]
    private void PauseAll() => _manager.PauseAll();

    private bool CanEnqueue() => Targets.Any(r => r.Status is DownloadStatus.Paused or DownloadStatus.Error);

    [RelayCommand(CanExecute = nameof(CanEnqueue))]
    private void Enqueue()
    {
        foreach (var row in Targets.ToList())
            _manager.Enqueue(row.Item);
    }

    private bool HasTargets() => Targets.Any();

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task Delete()
    {
        var rows = Targets.ToList();
        string what = rows.Count == 1 ? $"„{rows[0].Name}“" : L.F("{0} Downloads", rows.Count);
        if (!_actions.Confirm(L.F("{0} aus der Liste entfernen?", what) + "\n\n" + L.T("Fertige Dateien bleiben erhalten, angefangene Teile werden gelöscht.")))
            return;
        foreach (var row in rows)
            await _manager.RemoveAsync(row.Item, deleteFile: false);
    }

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task DeleteWithFile()
    {
        var rows = Targets.ToList();
        string what = rows.Count == 1 ? $"„{rows[0].Name}“" : L.F("{0} Downloads", rows.Count);
        if (!_actions.Confirm(L.F(rows.Count == 1 ? "{0} entfernen und die Datei von der Festplatte löschen?" : "{0} entfernen und die Dateien von der Festplatte löschen?", what)))
            return;
        foreach (var row in rows)
            await _manager.RemoveAsync(row.Item, deleteFile: true);
    }

    [RelayCommand(CanExecute = nameof(HasTargets))]
    private async Task Restart()
    {
        var rows = Targets.ToList();
        if (!_actions.Confirm(L.T("Die ausgewählten Downloads verwerfen und von vorne laden?")))
            return;
        foreach (var row in rows)
            await _manager.RestartAsync(row.Item);
    }

    private bool CanOpenFile() => SelectedItem is { IsCompleted: true, FilePath: not null };

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private void OpenFile() => Shell.Open(SelectedItem!.FilePath!);

    private bool CanOpenFolder() => SelectedItem is not null;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        var row = SelectedItem!;
        if (row.FilePath is { } path && File.Exists(path))
            Shell.ShowInFolder(path);
        else
            Shell.Open(row.Item.Directory);
    }

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void CopyUrl() => Clipboard.SetText(SelectedItem!.Url);

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void ShowDetails() => _actions.ShowProgress(SelectedItem!.Item);

    private bool CanUpdateAddress() => SelectedItem is { IsCompleted: false };

    [RelayCommand(CanExecute = nameof(CanUpdateAddress))]
    private void UpdateAddress() => _actions.UpdateAddress(SelectedItem!.Item);

    /// <summary>Doppelklick: fertige Datei öffnen, sonst Fortschrittsfenster.</summary>
    public void Activate(DownloadItemViewModel row)
    {
        if (row.IsCompleted && row.FilePath is { } path && File.Exists(path))
            Shell.Open(path);
        else
            _actions.ShowProgress(row.Item);
    }

    /// <summary>Startet die im Baum gewählte Warteschlange, sonst die Hauptwarteschlange.</summary>
    [RelayCommand(CanExecute = nameof(CanStartQueue))]
    private void StartQueue() => _manager.StartQueue(SelectedQueue);

    private DownloadQueue SelectedQueue =>
        SelectedFilter?.Path is { } path && path.StartsWith("Warteschlange/", StringComparison.Ordinal)
        && long.TryParse(path["Warteschlange/".Length..], out long id) && _manager.Queues.FirstOrDefault(q => q.Id == id) is { } queue
            ? queue
            : _manager.MainQueue;

    private bool CanStartQueue()
    {
        var queue = SelectedQueue;
        return !_manager.IsRunning(queue) && (queue.Sync || Items.Any(r => r.Status == DownloadStatus.Queued && r.Item.QueueId == queue.Id));
    }

    [RelayCommand]
    private void ShowScheduler() => _actions.ShowScheduler();

    [RelayCommand]
    private void ShowGrabber() => _actions.ShowGrabber();

    [RelayCommand]
    private void ImportList() => _actions.ImportList();

    /// <summary>Mehrere markiert: nur diese, sonst die ganze Liste.</summary>
    [RelayCommand]
    private void ExportList() =>
        _actions.ExportList((SelectedItems.Count > 1 ? SelectedItems : Items).Select(r => r.Item).ToList());

    [RelayCommand]
    private void EnqueueTo(DownloadQueue? queue)
    {
        if (queue is null)
            return;
        foreach (var row in Targets.Where(r => !r.IsCompleted).ToList())
        {
            if (row.IsActive)
                _manager.MoveToQueue(row.Item, queue);
            else
                _manager.Enqueue(row.Item, queue);
        }
        UpdateCounts();
    }

    [RelayCommand(CanExecute = nameof(QueueRunning))]
    private void StopQueue() => _manager.StopQueue();

    partial void OnSelectedItemChanged(DownloadItemViewModel? value) => NotifyCommands();

    private static void OnUi(Action action) => Application.Current.Dispatcher.BeginInvoke(action);
}
