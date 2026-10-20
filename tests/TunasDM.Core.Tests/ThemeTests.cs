using System.Text.RegularExpressions;

namespace TunasDM.Core.Tests;

public class ThemeTests
{
    /// <summary>
    /// Theme.Apply tauscht die eigenen Farben erst nach dem Laden von App.xaml aus. Stile dort müssen sie daher
    /// dynamisch beziehen, sonst behalten sie die dunklen Werte (helle Schrift auf hellem Grund).
    /// </summary>
    [Fact]
    public void AppStyles_UseThemeBrushesDynamically()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TunasDM.App");
        string theme = File.ReadAllText(Path.Combine(folder, "Theme.cs"));
        var keys = Regex.Matches(theme, @"Set(?:Color)?\(""(\w+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("TextSecondary", keys);

        string app = File.ReadAllText(Path.Combine(folder, "App.xaml"));
        var wrong = keys.Where(k => app.Contains("{StaticResource " + k + "}")).ToList();
        Assert.Empty(wrong);
    }
}
