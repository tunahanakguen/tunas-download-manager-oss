using System.Windows;
using TunasDM.Core;
using Forms = System.Windows.Forms;

namespace TunasDM.App.Services;

/// <summary>Symbol im Infobereich mit Menü; Doppelklick öffnet das Hauptfenster.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action open, Action addUrl, Action pauseAll, Action startQueue, Action scheduler, Action checkUpdates, Action exit)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/TunasDownloadManager.ico"))!.Stream;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("TUNAS Download Manager öffnen"), null, (_, _) => open());
        menu.Items.Add(L.T("URL hinzufügen …"), null, (_, _) => addUrl());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Hauptwarteschlange starten"), null, (_, _) => startQueue());
        menu.Items.Add(L.T("Zeitplaner …"), null, (_, _) => scheduler());
        menu.Items.Add(L.T("Alle pausieren"), null, (_, _) => pauseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Nach Updates suchen …"), null, (_, _) => checkUpdates());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Beenden"), null, (_, _) => exit());

        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "TUNAS Download Manager",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => open();
    }

    public void SetToolTip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
