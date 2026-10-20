using System.Windows;
using System.Windows.Threading;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

/// <summary>Vor dem Herunterfahren o. Ä. eine Minute Zeit zum Abbrechen. „Abbrechen“ ist vorausgewählt.</summary>
public partial class CountdownWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DispatcherTimer _timer;
    private int _left;

    public CountdownWindow(DownloadQueue queue, int seconds = 60)
    {
        InitializeComponent();
        _left = seconds;
        Bar.Maximum = seconds;
        string name = queue.DisplayName;
        MessageText.Text = queue.After switch
        {
            AfterQueueAction.Shutdown => L.F("Die Warteschlange „{0}“ ist fertig. Gleich fährt der Computer herunter.", name),
            AfterQueueAction.Hibernate => L.F("Die Warteschlange „{0}“ ist fertig. Gleich geht der Computer in den Ruhezustand.", name),
            AfterQueueAction.Sleep => L.F("Die Warteschlange „{0}“ ist fertig. Gleich geht der Computer in den Energiesparmodus.", name),
            _ => L.F("Die Warteschlange „{0}“ ist fertig. Gleich wird TUNAS beendet.", name),
        };
        NowButton.Content = queue.After switch
        {
            AfterQueueAction.Shutdown => L.T("Jetzt herunterfahren"),
            AfterQueueAction.Hibernate => L.T("Jetzt Ruhezustand"),
            AfterQueueAction.Sleep => L.T("Jetzt Energiesparen"),
            _ => L.T("Jetzt beenden"),
        };
        Show(_left);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick(), Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>True: Aktion ausführen (Zeit abgelaufen oder „Jetzt“).</summary>
    public bool Proceed { get; private set; }

    private void Tick()
    {
        if (--_left <= 0)
        {
            Proceed = true;
            Close();
            return;
        }
        Show(_left);
    }

    private void Show(int seconds)
    {
        CountText.Text = $"{seconds} s";
        Bar.Value = Bar.Maximum - seconds;
    }

    private void OnNow(object sender, RoutedEventArgs e)
    {
        Proceed = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
