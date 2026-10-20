using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using TunasDM.Core.Management;

namespace TunasDM.App.Services;

/// <summary>
/// Nur eine Instanz pro Windows-Sitzung. Weitere Aufrufe (z. B. <c>TunasDM.exe /d URL</c>)
/// reichen ihre Argumente über eine Named Pipe an die laufende Instanz weiter und beenden sich.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = $"TunasDM.App.{Process.GetCurrentProcess().SessionId}";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, $@"Local\{Id}", out bool created);
        if (created)
            return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>Argumente an die laufende Instanz schicken.</summary>
    public static bool Send(CommandLineRequest request)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Id, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(JsonSerializer.Serialize(request.ToArguments()));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Logger.Write("Weitergabe an laufende Instanz fehlgeschlagen: " + ex.Message);
            return false;
        }
    }

    /// <summary>Wartet auf Aufrufe weiterer Instanzen; <paramref name="onRequest"/> läuft im Hintergrund-Thread.</summary>
    public void Listen(Action<CommandLineRequest> onRequest)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stop.Token);
                    using var reader = new StreamReader(pipe);
                    if (await reader.ReadLineAsync(_stop.Token) is { } line
                        && JsonSerializer.Deserialize<string[]>(line) is { } args)
                        onRequest(CommandLineRequest.Parse(args));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    Logger.Write("Fehler beim Empfang eines Aufrufs: " + ex.Message);
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
