using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TunasDM.Core.Management;
using Wpf.Ui.Controls;

namespace TunasDM.App.ViewModels;

/// <summary>Knoten im Baum links: „Alle“, „Unfertig“, „Fertig“ – jeweils mit den Kategorien darunter.</summary>
public sealed partial class FilterViewModel : ObservableObject
{
    public FilterViewModel(string name, SymbolRegular symbol, Func<DownloadItem, bool> match)
    {
        Name = name;
        Symbol = symbol;
        Match = match;
    }

    public string Name { get; }
    public SymbolRegular Symbol { get; }
    public Func<DownloadItem, bool> Match { get; }
    public ObservableCollection<FilterViewModel> Children { get; } = new();

    /// <summary>Eindeutiger Pfad, z. B. „Unfertig/Video“, um die Auswahl nach Änderungen wiederzufinden.</summary>
    public string Path { get; init; } = "";

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public IEnumerable<FilterViewModel> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    public static SymbolRegular SymbolFor(string category) => category switch
    {
        "Video" => SymbolRegular.Video24,
        "Musik" => SymbolRegular.MusicNote224,
        "Dokumente" => SymbolRegular.Document24,
        "Programme" => SymbolRegular.Apps24,
        "Archive" => SymbolRegular.Archive24,
        "Bilder" => SymbolRegular.Image24,
        _ => SymbolRegular.Folder24,
    };
}
