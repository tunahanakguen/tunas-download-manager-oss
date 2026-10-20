using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using TunasDM.Core.Browser;
using TunasDM.Core.Management;

namespace TunasDM.App.Services;

/// <summary>
/// Pipe-Server für die Browser-Erweiterungen. Jeder Browser startet seinen eigenen TunasDM.Host.exe,
/// darum sind mehrere gleichzeitige Verbindungen erlaubt. Eine JSON-Nachricht pro Zeile.
/// </summary>
public sealed class BrowserBridge : IDisposable
{
    private readonly string _pipeName = BrowserProtocol.PipeName(Process.GetCurrentProcess().SessionId);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<StreamWriter> _clients = new();
    private readonly object _lock = new();
    private readonly Func<AppSettings> _settings;

    public BrowserBridge(Func<AppSettings> settings) => _settings = settings;

    /// <summary>Nachricht einer Erweiterung; kommt aus einem Hintergrund-Thread.</summary>
    public event EventHandler<BrowserMessage>? MessageReceived;

    public int ConnectedBrowsers
    {
        get
        {
            lock (_lock)
                return _clients.Count;
        }
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    /// <summary>Geänderte Einstellungen an alle verbundenen Erweiterungen schicken.</summary>
    public void PushConfig()
    {
        string line = BrowserProtocol.Serialize(BrowserProtocol.CreateConfig(_settings()));
        List<StreamWriter> clients;
        lock (_lock)
            clients = _clients.ToList();
        foreach (var client in clients)
            _ = SendAsync(client, line);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            catch (IOException ex)
            {
                Logger.Write("Browser-Pipe: " + ex.Message);
                await pipe.DisposeAsync();
                await Task.Delay(500);
                continue;
            }
            _ = Task.Run(() => ServeAsync(pipe));
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        lock (_lock)
            _clients.Add(writer);
        Logger.Write("Browser verbunden.");
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (BrowserProtocol.Parse(line) is not { } message)
                {
                    Logger.Write("Unlesbare Nachricht vom Browser verworfen.");
                    continue;
                }
                Logger.Write($"Vom Browser: {message.Type}{(message.Browser is { } b ? $" ({b} {message.ExtVersion})" : "")}{(message.Text is { } t ? $" – {t}" : "")}");
                if (message.Type == "diag")
                    continue;
                if (message.Type == "hello")
                    await SendAsync(writer, BrowserProtocol.Serialize(BrowserProtocol.CreateConfig(_settings())));
                else
                    MessageReceived?.Invoke(this, message);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Browser geschlossen oder App beendet.
        }
        finally
        {
            Logger.Write("Browser getrennt.");
            lock (_lock)
                _clients.Remove(writer);
            await pipe.DisposeAsync();
        }
    }

    private static async Task SendAsync(StreamWriter writer, string line)
    {
        try
        {
            await writer.WriteLineAsync(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Verbindung gerade getrennt.
        }
    }

    public void Dispose() => _stop.Cancel();
}
