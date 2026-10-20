using System.Windows;
using System.Windows.Controls;
using TunasDM.App.Services;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

/// <summary>
/// „Adresse aktualisieren“: neue Adresse für einen abgelaufenen Link. Solange das Fenster offen ist,
/// reicht die App den nächsten Browser-Download hierher (<see cref="TakeFromBrowser"/>).
/// </summary>
public partial class UpdateAddressWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly DownloadItem _item;
    private NewDownload? _fromBrowser;

    public UpdateAddressWindow(DownloadItem item)
    {
        InitializeComponent();
        _item = item;
        NameText.Text = item.DisplayName;
        StateText.Text = (item.TotalSize > 0 ? L.F("{0} von {1} geladen", ByteSize.Format(item.Downloaded), ByteSize.Format(item.TotalSize)) : L.T("Noch nichts geladen"))
            + (item.Error is { } error ? $" · {error}" : "");
        OpenPageButton.IsEnabled = PageUrl is not null;
    }

    /// <summary>Neue Adresse mit Cookies, Referrer und User-Agent (vom Browser) oder nur Adresse (eingefügt).</summary>
    public NewDownload? Result { get; private set; }

    private Uri? PageUrl =>
        Uri.TryCreate(_item.Referrer, UriKind.Absolute, out var page) && page.Scheme is "http" or "https" ? page : null;

    /// <summary>Download aus dem Browser übernehmen, statt ihn neu anzulegen.</summary>
    public void TakeFromBrowser(NewDownload download)
    {
        _fromBrowser = download;
        UrlBox.Text = download.Url.ToString();
        Hint.Text = L.T(download.Cookies is null ? "Vom Browser übernommen." : "Vom Browser übernommen, mit Anmeldung (Cookies).")
            + (download.FileName is { } name && !string.Equals(name, _item.DisplayName, StringComparison.OrdinalIgnoreCase)
                ? " " + L.F("Achtung: Der Browser nennt die Datei „{0}“ – prüfe, ob es dieselbe ist.", name) : "");
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnOpenPage(object sender, RoutedEventArgs e)
    {
        if (PageUrl is { } page)
            Shell.OpenUrl(page);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e) => OkButton.IsEnabled = AddUrlWindow.TryParse(UrlBox.Text) is not null;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (AddUrlWindow.TryParse(UrlBox.Text) is not { } url)
            return;
        // Cookies nur, wenn die Adresse wirklich die vom Browser ist (nicht nachträglich von Hand geändert).
        Result = _fromBrowser is { } browser && browser.Url == url ? browser : new NewDownload(url);
        DialogResult = true;
    }
}
