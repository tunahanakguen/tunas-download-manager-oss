using System.Windows;
using TunasDM.Core;

namespace TunasDM.App.Views;

/// <summary>Dunkle Rückfrage statt der hellen Windows-MessageBox.</summary>
public partial class ConfirmWindow : Wpf.Ui.Controls.FluentWindow
{
    /// <param name="yes">Text der Bestätigung (übersetzt übergeben); null = „Ja“.</param>
    /// <param name="no">Text der Ablehnung; leer = „Abbrechen“, null = kein zweiter Knopf.</param>
    public ConfirmWindow(string message, string? yes = null, string? no = "")
    {
        InitializeComponent();
        MessageText.Text = message;
        YesButton.Content = yes ?? L.T("Ja");
        if (no is null)
            NoButton.Visibility = Visibility.Collapsed;
        else
            NoButton.Content = no.Length > 0 ? no : L.T("Abbrechen");
    }

    public static bool Ask(Window? owner, string message, string? yes = null, string? no = "")
    {
        var window = new ConfirmWindow(message, yes, no);
        if (owner is { IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return window.ShowDialog() == true;
    }

    public static void Inform(Window? owner, string message)
    {
        var window = new ConfirmWindow(message, L.T("OK"), no: null);
        if (owner is { IsVisible: true })
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
    }

    private void OnYes(object sender, RoutedEventArgs e) => DialogResult = true;
}
