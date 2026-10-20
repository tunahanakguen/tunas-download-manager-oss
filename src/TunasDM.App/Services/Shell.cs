using System.Diagnostics;
using Microsoft.Win32;

namespace TunasDM.App.Services;

public static class Shell
{
    public static void Open(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Webseite im Standardbrowser öffnen (nur http/https).</summary>
    public static void OpenUrl(Uri url)
    {
        if (url.Scheme is "http" or "https")
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Explorer öffnen und die Datei markieren.</summary>
    public static void ShowInFolder(string path)
    {
        if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
            Open(folder);
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "TUNAS Download Manager";

    /// <summary>Ob TUNAS mit Windows startet (auch vom Installer eingetragen).</summary>
    public static bool IsStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is string { Length: > 0 };
    }

    /// <summary>Autostart über HKCU\...\Run, gestartet nur im Infobereich.</summary>
    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" /tray");
        else if (key.GetValue(RunValue) is not null)
            key.DeleteValue(RunValue);
    }
}
