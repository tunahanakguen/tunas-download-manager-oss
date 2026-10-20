using System.Windows;
using TunasDM.App.Services;
using TunasDM.Core;
using TunasDM.Core.Management;

namespace TunasDM.App.Views;

public partial class CompleteWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly string _path;

    public CompleteWindow(DownloadItem item)
    {
        InitializeComponent();
        _path = item.FilePath ?? "";
        NameText.Text = item.DisplayName;
        InfoText.Text = $"{ByteSize.Format(Math.Max(0, item.TotalSize))}  ·  {L.T(item.Category)}";
        PathText.Text = _path;
    }

    /// <summary>Der Nutzer möchte dieses Fenster künftig nicht mehr sehen.</summary>
    public bool DontShowAgain => DontShow.IsChecked == true;

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        Shell.Open(_path);
        Close();
    }

    private void OnFolder(object sender, RoutedEventArgs e)
    {
        Shell.ShowInFolder(_path);
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
