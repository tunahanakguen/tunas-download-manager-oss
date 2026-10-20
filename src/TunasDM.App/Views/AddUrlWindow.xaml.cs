using System.Windows;
using System.Windows.Controls;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

public partial class AddUrlWindow : Wpf.Ui.Controls.FluentWindow
{
    private static string DefaultHint => L.T("Nur http-, https- und ftp-Adressen. Batch: Platzhalter wie datei[01-99].zip oder bild[a-z].jpg.");

    public AddUrlWindow(string? initial)
    {
        InitializeComponent();

        // Einen Link aus der Zwischenablage gleich vorschlagen.
        if (initial is null && Clipboard.ContainsText() && TryParse(Clipboard.GetText()) is not null)
            initial = Clipboard.GetText().Trim();
        UrlBox.Text = initial ?? "";
        UrlBox.SelectAll();
        Hint.Text = DefaultHint;
    }

    public Uri? Url { get; private set; }

    /// <summary>Bei einem Batch-Muster alle erzeugten Adressen (sonst leer).</summary>
    public IReadOnlyList<Uri> BatchUrls { get; private set; } = [];

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        string text = UrlBox.Text.Trim();
        if (BatchPattern.HasPattern(text))
        {
            long count = BatchPattern.Count(text);
            bool valid = count is > 0 and <= BatchPattern.MaxLinks && TryParse(BatchPattern.Expand(text)[0]) is not null;
            OkButton.IsEnabled = valid;
            Hint.Text = count == 0 ? L.T("Ungültiger Platzhalter. Beispiele: [1-20], [01-20], [0-100:5], [a-z].")
                : count > BatchPattern.MaxLinks ? L.F("Das ergibt {0:N0} Adressen; höchstens {1:N0} sind möglich.", count, BatchPattern.MaxLinks)
                : valid ? L.F("Batch-Download: {0:N0} Adressen", count) : L.T("Keine gültige Adresse.");
            return;
        }
        OkButton.IsEnabled = TryParse(text) is not null;
        Hint.Text = DefaultHint;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string text = UrlBox.Text.Trim();
        if (BatchPattern.HasPattern(text))
        {
            try
            {
                BatchUrls = BatchPattern.Expand(text).Select(TryParse).OfType<Uri>().ToList();
            }
            catch (FormatException ex)
            {
                Hint.Text = ex.Message;
                return;
            }
            DialogResult = BatchUrls.Count > 0;
            return;
        }
        Url = TryParse(text);
        DialogResult = Url is not null;
    }

    public static Uri? TryParse(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && DownloadSchemes.IsSupported(uri) ? uri : null;
}
