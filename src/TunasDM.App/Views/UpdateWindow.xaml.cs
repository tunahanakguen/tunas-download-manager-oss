using System.Windows;
using TunasDM.App.Services;
using TunasDM.Core;
using TunasDM.Core.Updates;

namespace TunasDM.App.Views;

/// <summary>Neue Fassung anbieten: Hinweise zeigen, Setup laden und prüfen, dann installieren.</summary>
public partial class UpdateWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly UpdateInfo _update;
    private readonly Func<ProxySettings> _proxy;
    private readonly CancellationTokenSource _cancel = new();
    private bool _downloading;

    public UpdateWindow(UpdateInfo update, Func<ProxySettings> proxy)
    {
        InitializeComponent();
        _update = update;
        _proxy = proxy;
        HeadText.Text = L.F("TUNAS Download Manager {0} ist da", update.Version.ToString(3));
        SubText.Text = L.F("Installiert: {0}", Updater.CurrentVersion.ToString(3)) + $"  ·  Setup {update.Size / 1048576.0:0} MB";
        NotesBox.Text = string.IsNullOrWhiteSpace(update.Notes) ? L.T("Keine Hinweise veröffentlicht.") : PlainText(update.Notes);
        if (!Updater.IsInstalled)
        {
            // Entwicklungsfassung: das Setup würde eine zweite Kopie installieren.
            InstallButton.Content = L.T("Seite öffnen");
            SubText.Text += "  ·  " + L.T("Diese Fassung ist nicht mit dem Setup installiert.");
        }
        Closing += (_, e) => e.Cancel = _downloading && !_cancel.IsCancellationRequested && !Confirm();
        Closed += (_, _) => _cancel.Cancel();
    }

    /// <summary>Hinweise von GitHub sind Markdown: Hervorhebungen und Code-Zeichen weg, Listenpunkte als „•“.</summary>
    internal static string PlainText(string markdown)
    {
        string text = markdown.Replace("\r\n", "\n").Replace("**", "").Replace("__", "").Replace("`", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*[-*]\s+", "• ", System.Text.RegularExpressions.RegexOptions.Multiline);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        return text.Trim();
    }

    /// <summary>„Diese Version überspringen“ gewählt.</summary>
    public bool Skipped { get; private set; }

    /// <summary>Setup geprüft und gestartet; TUNAS soll sich jetzt beenden.</summary>
    public string? SetupPath { get; private set; }

    private bool Confirm() => ConfirmWindow.Ask(this, L.T("Download des Updates abbrechen?"), L.T("Abbrechen"), L.T("Weiter laden"));

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Skipped = true;
        Close();
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (!Updater.IsInstalled)
        {
            Shell.OpenUrl(_update.Page ?? _update.SetupUrl);
            Close();
            return;
        }

        _downloading = true;
        InstallButton.IsEnabled = SkipButton.IsEnabled = false;
        LaterButton.Content = L.T("Abbrechen");
        ErrorText.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = L.T("Update wird geladen …");
        var progress = new Progress<(long Received, long Total)>(p =>
        {
            Bar.Value = p.Total > 0 ? (double)p.Received / p.Total : 0;
            ProgressText.Text = L.F("Update wird geladen … {0:0.0} von {1:0.0} MB", p.Received / 1048576.0, p.Total / 1048576.0);
        });
        try
        {
            SetupPath = await Updater.DownloadAsync(_update, _proxy(), progress, _cancel.Token);
            ProgressText.Text = L.T("Geprüft. TUNAS wird für das Update beendet …");
            _downloading = false;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _downloading = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ErrorText.Text = L.T("Update fehlgeschlagen:") + " " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            InstallButton.IsEnabled = SkipButton.IsEnabled = true;
            LaterButton.Content = L.T("Später");
        }
    }
}
