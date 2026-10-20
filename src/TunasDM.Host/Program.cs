using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using TunasDM.Host;

// Vom Browser gestartet (Argument: chrome-extension://…/). Reicht Nachrichten 1:1 weiter:
// Browser ⇄ (stdio, Längenpräfix) ⇄ Host ⇄ (Named Pipe, eine JSON-Zeile je Nachricht) ⇄ TunasDM.exe
string appPath = AppPath(AppContext.BaseDirectory);
string pipeName = $"TunasDM.Browser.{Process.GetCurrentProcess().SessionId}";
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
HostLog.Write($"Start: {string.Join(' ', args)}; im Job: {Native.InJob()}");

NamedPipeClientStream pipe;
try
{
    pipe = await ConnectAsync(pipeName, appPath);
    HostLog.Write("Mit der App verbunden.");
}
catch (Exception ex)
{
    HostLog.Write("Verbindung fehlgeschlagen: " + ex);
    await SendErrorAsync(stdout, ex.Message);
    return 1;
}

uint appProcessId = Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid) ? serverPid : 0;

using (pipe)
{
    var toApp = Task.Run(async () =>
    {
        // Browser → App
        while (await NativeMessaging.ReadAsync(stdin) is { } message)
        {
            if (message.AsSpan().IndexOfAny((byte)'\n', (byte)'\r') >= 0)
                continue; // JSON.stringify erzeugt keine Zeilenumbrüche; alles andere ist kaputt.
            // Nur den Typ protokollieren: Nachrichten können Cookies enthalten.
            var type = System.Text.RegularExpressions.Regex.Match(Encoding.UTF8.GetString(message), "\"type\"\\s*:\\s*\"(\\w+)\"");
            HostLog.Write($"Browser → App: {(type.Success ? type.Groups[1].Value : "?")} ({message.Length} Bytes)");
            // Windows lässt nur den Vordergrund-Prozess (den Browser) und seine Kinder Fenster nach vorn holen:
            // Bei Aktionen, auf die die App mit einem Fenster antwortet, dieses Recht an die App weitergeben.
            if (appProcessId != 0 && type.Groups[1].Value is "download" or "links" or "grab" or "media")
                Native.AllowSetForegroundWindow(appProcessId);
            await pipe.WriteAsync(message);
            await pipe.WriteAsync("\n"u8.ToArray());
            await pipe.FlushAsync();
        }
    });
    var toBrowser = Task.Run(async () =>
    {
        // App → Browser
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        while (await reader.ReadLineAsync() is { } line)
            await NativeMessaging.WriteAsync(stdout, Encoding.UTF8.GetBytes(line));
    });

    // Endet eine Seite (Browser schließt den Port oder App wird beendet), endet der Host.
    var finished = await Task.WhenAny(toApp, toBrowser);
    HostLog.Write(finished == toApp ? "Browser hat getrennt." : "App hat getrennt.");
    if (finished.IsFaulted)
    {
        HostLog.Write("Fehler: " + finished.Exception!.InnerException);
        await SendErrorAsync(stdout, finished.Exception!.InnerException!.Message);
    }
}
return 0;

// Neben der App liegt TunasDM.exe direkt daneben; die Kopie unter %LocalAppData% nennt den Pfad in TunasDM.app.txt.
static string AppPath(string hostFolder)
{
    string beside = Path.Combine(hostFolder, "TunasDM.exe");
    if (File.Exists(beside))
        return beside;
    string pointer = Path.Combine(hostFolder, "TunasDM.app.txt");
    return File.Exists(pointer) && File.ReadAllText(pointer).Trim() is { Length: > 0 } path && Path.IsPathFullyQualified(path)
        ? path
        : beside;
}

static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, string appPath)
{
    if (await TryConnectAsync(pipeName, appPath, TimeSpan.FromMilliseconds(500)) is { } pipe)
        return pipe;

    // App läuft nicht: im Infobereich starten und warten, bis die Pipe bereit ist.
    if (!File.Exists(appPath))
        throw new FileNotFoundException("TUNAS Download Manager wurde nicht gefunden.", appPath);
    Native.StartDetached(appPath, "/tray");

    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(20))
    {
        if (await TryConnectAsync(pipeName, appPath, TimeSpan.FromSeconds(1)) is { } started)
            return started;
    }
    throw new TimeoutException("TUNAS Download Manager antwortet nicht.");
}

static async Task<NamedPipeClientStream?> TryConnectAsync(string pipeName, string appPath, TimeSpan timeout)
{
    var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    try
    {
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds);
    }
    catch (Exception ex) when (ex is TimeoutException or IOException)
    {
        pipe.Dispose();
        return null;
    }

    // Nur mit dem echten Programm sprechen, nicht mit irgendeinem Prozess, der die Pipe belegt.
    if (!ServerIs(pipe, appPath))
    {
        pipe.Dispose();
        throw new UnauthorizedAccessException("Die Pipe gehört nicht zum TUNAS Download Manager.");
    }
    return pipe;
}

static bool ServerIs(NamedPipeClientStream pipe, string expectedPath)
{
    if (!Native.GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid))
        return false;
    try
    {
        using var process = Process.GetProcessById((int)pid);
        return string.Equals(process.MainModule?.FileName, expectedPath, StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
    {
        return false;
    }
}

static async Task SendErrorAsync(Stream stdout, string message)
{
    string json = $$"""{"type":"error","message":"{{System.Text.Json.JsonEncodedText.Encode(message)}}"}""";
    try
    {
        await NativeMessaging.WriteAsync(stdout, Encoding.UTF8.GetBytes(json));
    }
    catch (IOException)
    {
        // Browser schon weg.
    }
}

internal static class Native
{
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNewProcessGroup = 0x00000200;
    private const uint DetachedProcess = 0x00000008;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Ptr, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    public static bool InJob() => IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out bool inJob) && inJob;

    /// <summary>
    /// Startet die App unabhängig vom Browser: ohne geerbte Handles (sonst hielte sie die Browser-Pipes offen)
    /// und möglichst außerhalb des Jobs, in den der Browser den Host steckt (sonst stirbt sie mit ihm).
    /// </summary>
    public static void StartDetached(string path, string arguments)
    {
        foreach (uint flags in new[] { CreateBreakawayFromJob | CreateNewProcessGroup | DetachedProcess, CreateNewProcessGroup | DetachedProcess })
        {
            var startup = new StartupInfo { Cb = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder($"\"{path}\" {arguments}");
            if (CreateProcessW(path, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                    Path.GetDirectoryName(path), ref startup, out var info))
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
                HostLog.Write($"App gestartet (PID {info.ProcessId}, Breakaway: {(flags & CreateBreakawayFromJob) != 0}).");
                return;
            }
            HostLog.Write($"CreateProcess mit Flags 0x{flags:X} fehlgeschlagen: Fehler {Marshal.GetLastWin32Error()}.");
        }
        throw new Win32Exception("TUNAS Download Manager konnte nicht gestartet werden.");
    }
}
