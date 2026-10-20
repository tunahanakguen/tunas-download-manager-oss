using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace TunasDM.App;

/// <summary>
/// TUNAS-Design auf WPF-UI: schwarz-weiß wie das Logo, dazu Blau als Erkennungsfarbe des Download Managers
/// (jede TUNAS-App hat ihre eigene; der Converter bleibt schwarz). Dunkel: helles Blau für Kontrast,
/// hell: das tiefe Markenblau. Gilt ab dem Start, weil die Fenster die Farben beim Laden übernehmen.
/// </summary>
public static class Theme
{
    /// <summary>„“ = wie Windows, „dark“, „light“.</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Modes =
        [("", "Wie Windows"), ("dark", "Dunkel"), ("light", "Hell")];

    public static bool IsLight { get; private set; }

    /// <summary>Markenblau des Logos (Hintergrund des Thunfischs).</summary>
    public static readonly Color BrandBlue = Color.FromRgb(0x0A, 0x3F, 0x82);

    /// <summary>Akzent der Oberfläche: im dunklen Design heller, damit Schrift und Schalter lesbar bleiben.</summary>
    public static Color AccentColor => IsLight ? Color.FromRgb(0x0B, 0x4F, 0xA3) : Color.FromRgb(0x5A, 0xA6, 0xFF);

    public static void Apply(string? mode)
    {
        IsLight = mode switch
        {
            "light" => true,
            "dark" => false,
            _ => WindowsUsesLightApps(),
        };
        var theme = IsLight ? ApplicationTheme.Light : ApplicationTheme.Dark;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.Mica, false);
        ApplicationAccentColorManager.Apply(AccentColor, theme, false, false);
        SetColor("Accent", AccentColor);
        SetColor("OnAccent", IsLight ? Colors.White : Color.FromRgb(0x05, 0x1A, 0x38));

        // Eigene Farben (App.xaml) für Hell umdrehen; halbtransparent, damit Mica durchscheint.
        if (IsLight)
        {
            Set("TextPrimary", 0xFF, 0x00);
            Set("TextSecondary", 0xC8, 0x00);
            Set("TextMuted", 0x8C, 0x00);
            Set("PanelBackground", 0x0A, 0x00);
            Set("PanelAlt", 0x10, 0x00);
            Set("HoverBackground", 0x0C, 0x00);
            Set("BorderBrush", 0x1A, 0x00);
            SetColor("Green", Color.FromRgb(0x1E, 0x7B, 0x34));
            SetColor("Danger", Color.FromRgb(0xC4, 0x2B, 0x1C));
        }
    }

    /// <summary>Windows-Einstellung „Standard-App-Modus“ (hell/dunkel).</summary>
    public static bool WindowsUsesLightApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 1;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void Set(string key, byte alpha, byte gray) => SetColor(key, Color.FromArgb(alpha, gray, gray, gray));

    private static void SetColor(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
