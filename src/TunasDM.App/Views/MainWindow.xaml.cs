using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TunasDM.App.ViewModels;
using Wpf.Ui.Controls;

namespace TunasDM.App.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FilterViewModel filter && filter != ViewModel.SelectedFilter)
            ViewModel.SelectFilter(filter);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SetSelection(List.SelectedItems.Cast<DownloadItemViewModel>().ToList());

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject)?.FindParent<System.Windows.Controls.ListViewItem>()?.DataContext is DownloadItemViewModel row)
            ViewModel.Activate(row);
    }

    /// <summary>„…“ an der Zeile: Zeile auswählen und das Kontextmenü am Knopf öffnen.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadItemViewModel row } button)
        {
            List.SelectedItem = row;
            RowMenu.PlacementTarget = button;
            RowMenu.Placement = PlacementMode.Bottom;
            RowMenu.IsOpen = true;
        }
    }

    /// <summary>„Liste“: Menü mit Importieren/Exportieren unter dem Knopf aufklappen.</summary>
    private void OnListMenu(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        button.ContextMenu.DataContext = DataContext;
        button.ContextMenu.IsOpen = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedUrl(e.Data) is not null || DroppedList(e.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Link aus dem Browser hierher ziehen = Download hinzufügen; eine Link-Liste (.txt, .ef2, .html) = importieren.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DroppedList(e.Data) is { } list)
            ViewModel.ImportFromDrop(list);
        else if (DroppedUrl(e.Data) is { } url)
            ViewModel.AddUrlFromDrop(url);
    }

    private static string? DroppedList(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files
        && Path.GetExtension(files[0]).ToLowerInvariant() is ".txt" or ".ef2" or ".htm" or ".html"
            ? files[0]
            : null;

    private static string? DroppedUrl(IDataObject data)
    {
        string? text = data.GetDataPresent("UniformResourceLocatorW") ? ReadUrlFormat(data, "UniformResourceLocatorW", System.Text.Encoding.Unicode)
            : data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string
            : null;
        text = text?.Trim().Split('\n')[0].Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && TunasDM.Core.DownloadSchemes.IsSupported(uri) ? uri.ToString() : null;
    }

    private static string? ReadUrlFormat(IDataObject data, string format, System.Text.Encoding encoding) =>
        data.GetData(format) is MemoryStream stream ? encoding.GetString(stream.ToArray()).TrimEnd('\0') : null;
}

internal static class VisualTreeExtensions
{
    public static T? FindParent<T>(this DependencyObject child) where T : DependencyObject
    {
        for (var current = child; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
                return match;
            if (current is not System.Windows.Media.Visual)
                return null;
        }
        return null;
    }
}
