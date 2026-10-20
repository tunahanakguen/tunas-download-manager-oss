using System.Windows;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

public enum DuplicateChoice
{
    Cancel,

    /// <summary>Unfertig: vorhandenen Eintrag fortsetzen. Fertig: Datei im Ordner zeigen.</summary>
    UseExisting,

    /// <summary>Als neuen Eintrag laden; die Engine vergibt einen freien Namen.</summary>
    AddNew,

    /// <summary>Alten Eintrag samt Datei entfernen, dann neu laden.</summary>
    Replace,
}

/// <summary>„Doppelter Download-Link“: dieselbe Adresse wird nicht stillschweigend zweimal geladen.</summary>
public partial class DuplicateWindow : Wpf.Ui.Controls.FluentWindow
{
    public DuplicateWindow(DownloadItem existing)
    {
        InitializeComponent();
        NameText.Text = existing.DisplayName;
        UrlText.Text = Core.Secrets.MaskPassword(existing.Url);
        bool done = existing.Status == DownloadStatus.Completed;
        StateText.Text = done
            ? L.F("Fertig geladen am {0}", existing.CompletedAt?.ToString("g", L.Culture)) + (existing.TotalSize > 0 ? $" · {ByteSize.Format(existing.TotalSize)}" : "")
            : existing.IsActive ? L.T("Wird gerade geladen")
            : existing.TotalSize > 0 ? L.F("Angefangen: {0} von {1}", ByteSize.Format(existing.Downloaded), ByteSize.Format(existing.TotalSize))
            : L.T("Noch nicht fertig");
        ResumeOption.Content = L.T(done ? "Vorhandene Datei im Ordner zeigen" : existing.IsActive ? "Den laufenden Download anzeigen" : "Vorhandenen Download fortsetzen");
        ResumeOption.IsChecked = true;
    }

    public DuplicateChoice Choice { get; private set; } = DuplicateChoice.Cancel;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Choice = ResumeOption.IsChecked == true ? DuplicateChoice.UseExisting
            : NewOption.IsChecked == true ? DuplicateChoice.AddNew
            : DuplicateChoice.Replace;
        DialogResult = true;
    }
}
