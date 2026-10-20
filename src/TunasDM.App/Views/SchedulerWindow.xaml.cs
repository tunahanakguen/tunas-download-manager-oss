using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using TunasDM.Core.Management;
using TunasDM.Core;

namespace TunasDM.App.Views;

/// <summary>Zeitplaner: Warteschlangen anlegen, Zeitplan, Parallelität, Wiederholungen und Aktion danach.</summary>
public partial class SchedulerWindow : Wpf.Ui.Controls.FluentWindow
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    private readonly DownloadManager _manager;
    private readonly ObservableCollection<QueueRow> _rows = new();
    private QueueRow? _current;
    private bool _switching;

    public SchedulerWindow(DownloadManager manager, DownloadQueue? select = null)
    {
        InitializeComponent();
        _manager = manager;

        foreach (var day in Week)
            DaysPanel.Children.Add(new CheckBox
            {
                Content = L.Culture.DateTimeFormat.GetAbbreviatedDayName(day),
                Tag = day,
                MinWidth = 64,
                Margin = new Thickness(0, 0, 4, 4),
            });
        ParallelBox.ItemsSource = Enumerable.Range(1, 10).ToArray();
        RetriesBox.ItemsSource = Enumerable.Range(0, 11).ToArray();

        foreach (var queue in manager.Queues)
            _rows.Add(new QueueRow(queue, manager));
        QueueList.ItemsSource = _rows;
        QueueList.SelectedItem = _rows.FirstOrDefault(r => r.Queue == select) ?? _rows[0];

        manager.QueueStateChanged += OnQueueStateChanged;
        Closed += (_, _) => manager.QueueStateChanged -= OnQueueStateChanged;
    }

    private void OnQueueStateChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateInfo);

    private void OnQueueSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_switching)
            return;
        // Erst die Änderungen der bisherigen Warteschlange übernehmen; ist etwas ungültig, dort bleiben.
        if (_current is not null && QueueList.SelectedItem != _current && !Save())
        {
            _switching = true;
            QueueList.SelectedItem = _current;
            _switching = false;
            return;
        }
        if (QueueList.SelectedItem is QueueRow row)
            Show(row);
    }

    private void Show(QueueRow row)
    {
        _current = row;
        var queue = row.Queue;
        var schedule = queue.Schedule;
        NameBox.Text = queue.Name;
        StartEnabled.IsChecked = schedule.StartEnabled;
        StartTimeBox.Text = schedule.StartTime.ToString(@"hh\:mm");
        OnceDate.SelectedDate = schedule.Date;
        OnceOption.IsChecked = schedule.Repeat == QueueRepeat.Once;
        DailyOption.IsChecked = schedule.Repeat == QueueRepeat.Daily;
        DaysOption.IsChecked = schedule.Repeat == QueueRepeat.Weekdays;
        foreach (CheckBox box in DaysPanel.Children)
            box.IsChecked = schedule.Days.Contains((DayOfWeek)box.Tag);
        StopEnabled.IsChecked = schedule.StopEnabled;
        StopTimeBox.Text = schedule.StopTime.ToString(@"hh\:mm");
        ParallelBox.SelectedItem = Math.Clamp(queue.MaxParallel, 1, 10);
        RetriesBox.SelectedItem = Math.Clamp(queue.Retries, 0, 10);
        AfterBox.SelectedIndex = (int)queue.After;
        ProgramBox.Text = queue.AfterProgram ?? "";
        ArgumentsBox.Text = queue.AfterArguments ?? "";
        SyncBox.IsChecked = queue.Sync;
        RemoveButton.IsEnabled = !queue.IsMain;
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateEnabled();
        UpdateInfo();
    }

    /// <summary>Formular in die Warteschlange schreiben. False bei ungültiger Eingabe (Hinweis wird gezeigt).</summary>
    private void OnAfterChanged(object sender, SelectionChangedEventArgs e) =>
        ProgramPanel.Visibility = AfterBox.SelectedIndex == (int)AfterQueueAction.RunProgram ? Visibility.Visible : Visibility.Collapsed;

    private void OnBrowseProgram(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Programm wählen"),
            Filter = L.T("Programme und Skripte (*.exe;*.bat;*.cmd;*.ps1)|*.exe;*.bat;*.cmd;*.ps1|Alle Dateien|*.*"),
        };
        if (dialog.ShowDialog(this) == true)
            ProgramBox.Text = dialog.FileName;
    }

    private bool Save()
    {
        if (_current is not { } row)
            return true;
        var queue = row.Queue;
        string name = NameBox.Text.Trim();
        if (name.Length == 0)
            return Fail(L.T("Die Warteschlange braucht einen Namen."));
        if (_rows.Any(r => r != row && string.Equals(r.Queue.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            return Fail(L.F("Es gibt schon eine Warteschlange „{0}“.", name));
        if (!TryParseTime(StartTimeBox.Text, out var start))
            return Fail(L.T("Die Startzeit bitte als Uhrzeit eingeben, z. B. 23:00."));
        if (!TryParseTime(StopTimeBox.Text, out var stop))
            return Fail(L.T("Die Stoppzeit bitte als Uhrzeit eingeben, z. B. 07:00."));
        var days = DaysPanel.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (DayOfWeek)b.Tag).ToList();
        var repeat = OnceOption.IsChecked == true ? QueueRepeat.Once : DaysOption.IsChecked == true ? QueueRepeat.Weekdays : QueueRepeat.Daily;
        if (StartEnabled.IsChecked == true && repeat == QueueRepeat.Weekdays && days.Count == 0)
            return Fail(L.T("Bitte mindestens einen Wochentag wählen."));
        var after = (AfterQueueAction)Math.Max(0, AfterBox.SelectedIndex);
        string program = ProgramBox.Text.Trim().Trim('"');
        if (after == AfterQueueAction.RunProgram && program.Length == 0)
            return Fail(L.T("Bitte das Programm angeben, das gestartet werden soll."));

        queue.Name = name;
        queue.MaxParallel = ParallelBox.SelectedItem is int parallel ? parallel : 3;
        queue.Retries = RetriesBox.SelectedItem is int retries ? retries : 3;
        queue.After = after;
        queue.AfterProgram = program.Length > 0 ? program : null;
        queue.AfterArguments = ArgumentsBox.Text.Trim() is { Length: > 0 } arguments ? arguments : null;
        queue.Sync = SyncBox.IsChecked == true;
        queue.Schedule = new QueueSchedule
        {
            StartEnabled = StartEnabled.IsChecked == true,
            Repeat = repeat,
            Date = OnceDate.SelectedDate ?? DateTime.Today,
            Days = days,
            StartTime = start,
            StopEnabled = StopEnabled.IsChecked == true,
            StopTime = stop,
        };
        _manager.UpdateQueue(queue);
        row.Refresh();
        ErrorText.Visibility = Visibility.Collapsed;
        return true;
    }

    private bool Fail(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        return false;
    }

    internal static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        string value = text.Trim().Replace('.', ':');
        if (!value.Contains(':') && int.TryParse(value, out int hour) && hour is >= 0 and < 24)
        {
            time = TimeSpan.FromHours(hour);
            return true;
        }
        return TimeSpan.TryParseExact(value, [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
    }

    private void OnScheduleToggled(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void OnRepeatChanged(object sender, RoutedEventArgs e) => UpdateEnabled();

    private void UpdateEnabled()
    {
        bool start = StartEnabled.IsChecked == true;
        StartTimeBox.IsEnabled = start;
        RepeatPanel.IsEnabled = start;
        OnceDate.IsEnabled = OnceOption.IsChecked == true;
        DaysPanel.IsEnabled = DaysOption.IsChecked == true;
        StopTimeBox.IsEnabled = StopEnabled.IsChecked == true;
    }

    private void UpdateInfo()
    {
        if (_current is not { } row)
            return;
        var queue = row.Queue;
        int waiting = _manager.Items.Count(i => i.QueueId == queue.Id && i.Status == DownloadStatus.Queued);
        bool running = _manager.IsRunning(queue);
        QueueInfo.Text = (waiting == 1 ? L.T("1 Download wartet") : L.F("{0} Downloads warten", waiting)) + (running ? " · " + L.T("läuft gerade") : "");
        StartButton.IsEnabled = !running && (waiting > 0 || queue.Sync);
        StopButton.IsEnabled = running;
        foreach (var r in _rows)
            r.Refresh();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!Save())
            return;
        int number = 2;
        while (_rows.Any(r => r.Queue.Name == L.F("Warteschlange {0}", number)))
            number++;
        var row = new QueueRow(_manager.AddQueue(L.F("Warteschlange {0}", number)), _manager);
        _rows.Add(row);
        QueueList.SelectedItem = row;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_current is not { Queue.IsMain: false } row)
            return;
        if (!ConfirmWindow.Ask(this, L.F("Warteschlange „{0}“ löschen?", row.Queue.DisplayName) + "\n\n" + L.T("Ihre Downloads wandern in die Hauptwarteschlange.")))
            return;
        _manager.RemoveQueue(row.Queue);
        _current = null;
        _rows.Remove(row);
        QueueList.SelectedItem = _rows[0];
    }

    private void OnStartNow(object sender, RoutedEventArgs e)
    {
        if (_current is { } row && Save())
            _manager.StartQueue(row.Queue);
        UpdateInfo();
    }

    private void OnStopNow(object sender, RoutedEventArgs e)
    {
        if (_current is { } row)
            _manager.StopQueue(row.Queue);
        UpdateInfo();
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (Save())
            Close();
    }

    /// <summary>Eintrag der Liste links: Name und Kurzfassung des Zeitplans.</summary>
    public sealed partial class QueueRow(DownloadQueue queue, DownloadManager manager) : ObservableObject
    {
        public DownloadQueue Queue { get; } = queue;

        public string Name => Queue.DisplayName;

        public string Summary
        {
            get
            {
                var s = Queue.Schedule;
                string when = !s.StartEnabled ? L.T("Ohne Zeitplan")
                    : s.Repeat switch
                    {
                        QueueRepeat.Once => L.F("Am {0} um {1}", s.Date.ToString("d.M.", L.Culture), s.StartTime.ToString(@"hh\:mm")),
                        QueueRepeat.Daily => L.F("Täglich {0}", s.StartTime.ToString(@"hh\:mm")),
                        _ => $"{string.Join(" ", Week.Where(s.Days.Contains).Select(d => L.Culture.DateTimeFormat.GetAbbreviatedDayName(d)))} {s.StartTime:hh\\:mm}",
                    };
                string after = Queue.After switch
                {
                    AfterQueueAction.Shutdown => " · " + L.T("dann aus"),
                    AfterQueueAction.Hibernate => " · " + L.T("dann Ruhezustand"),
                    AfterQueueAction.Sleep => " · " + L.T("dann Energiesparen"),
                    AfterQueueAction.ExitApp => " · " + L.T("dann beenden"),
                    AfterQueueAction.RunProgram => " · " + L.F("dann {0}", Path.GetFileName(Queue.AfterProgram)),
                    _ => "",
                };
                return (manager.IsRunning(Queue) ? L.T("Läuft") + " · " : "") + (Queue.Sync ? "Sync · " : "") + when + after;
            }
        }

        public void Refresh()
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Summary));
        }
    }
}
