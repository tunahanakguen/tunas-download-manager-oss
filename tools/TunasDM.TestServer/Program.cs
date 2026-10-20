using TunasDM.Core.Tests;

// tunasdm-testserver [GrößeMB] [Port] [VerzögerungMs]
// Liefert eine erzeugte Datei mit Range-Unterstützung, z. B. http://127.0.0.1:8765/files/test.bin
long sizeMb = args.Length > 0 && long.TryParse(args[0], out var s) ? s : 200;
int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 8765;
int delay = args.Length > 2 && int.TryParse(args[2], out var d) ? d : 2;

await using var server = await TestFileServer.StartAsync(sizeMb * 1024 * 1024, port);
server.ChunkDelayMs = delay;
server.ContentDisposition = $"attachment; filename=\"testdatei-{sizeMb}MB.zip\"";
Console.WriteLine($"Testserver: {server.FileUri}  ({sizeMb} MB, {delay} ms je 16 KB pro Verbindung). Beenden mit Strg+C.");

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    done.TrySetResult();
};
await done.Task;
