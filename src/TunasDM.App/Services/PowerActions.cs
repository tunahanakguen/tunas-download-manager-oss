using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TunasDM.App.Services;

/// <summary>Energiesparmodus, Ruhezustand und Herunterfahren nach einer Warteschlange.</summary>
public static class PowerActions
{
    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public static void Sleep() => Suspend(hibernate: false);

    public static void Hibernate() => Suspend(hibernate: true);

    private static void Suspend(bool hibernate)
    {
        if (!SetSuspendState(hibernate, false, false))
            Logger.Write($"{(hibernate ? "Ruhezustand" : "Energiesparmodus")} fehlgeschlagen: Fehler {Marshal.GetLastWin32Error()}.");
    }

    /// <summary>Windows normal herunterfahren (Programme dürfen sich noch beenden).</summary>
    public static void Shutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Write("Herunterfahren fehlgeschlagen: " + ex.Message);
        }
    }
}
