using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TunasDM.Core.Management;

namespace TunasDM.App.Services;

/// <summary>Zwischenablage überwachen: meldet kopierte Links auf Dateien mit bekannter Endung.</summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private readonly HwndSource _source;
    private readonly Func<AppSettings> _settings;
    private string? _last;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public ClipboardMonitor(Window window, Func<AppSettings> settings)
    {
        _settings = settings;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())!;
        _source.AddHook(Hook);
        AddClipboardFormatListener(_source.Handle);
    }

    public event EventHandler<Uri>? UrlCopied;

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && _settings().MonitorClipboard)
        {
            try
            {
                if (Clipboard.ContainsText() && TryGetDownloadUrl(Clipboard.GetText(), _settings().Categories) is { } url
                    && url.ToString() != _last)
                {
                    _last = url.ToString();
                    UrlCopied?.Invoke(this, url);
                }
            }
            catch (COMException)
            {
                // Zwischenablage gerade von einem anderen Programm belegt.
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Nur einzelne http(s)-Links, deren Endung zu einer Kategorie gehört.</summary>
    public static Uri? TryGetDownloadUrl(string text, IReadOnlyList<Category> categories)
    {
        text = text.Trim();
        if (text.Length > 2048 || text.Contains('\n') || !Uri.TryCreate(text, UriKind.Absolute, out var url)
            || !TunasDM.Core.DownloadSchemes.IsSupported(url))
            return null;
        string name = Path.GetFileName(url.AbsolutePath);
        return categories.Any(c => c.Name != Category.General && c.Matches(name)) ? url : null;
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(Hook);
    }
}
