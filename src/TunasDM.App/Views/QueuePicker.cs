using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TunasDM.Core.Management;
using TunasDM.Core;

namespace TunasDM.App.Views;

/// <summary>
/// „Später laden“: Gibt es mehrere Warteschlangen, klappt unter dem Knopf eine Auswahl auf;
/// bei nur einer geht es direkt in die Hauptwarteschlange.
/// </summary>
public static class QueuePicker
{
    public static void Pick(FrameworkElement anchor, IReadOnlyList<DownloadQueue> queues, Action<DownloadQueue> picked)
    {
        if (queues.Count <= 1)
        {
            picked(queues.Count == 1 ? queues[0] : DownloadQueue.CreateMain(3));
            return;
        }
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = L.T("In Warteschlange stellen:"), IsEnabled = false });
        foreach (var queue in queues)
        {
            var item = new MenuItem { Header = queue.DisplayName };
            item.Click += (_, _) => picked(queue);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
