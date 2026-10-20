using System.Text;
using TunasDM.Core;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("""
        TUNAS Download Manager – Konsole (Phase 1)

        tunasdm <URL> [-o Ordner] [-n Dateiname] [-c Verbindungen] [-l Limit]

          -o  Zielordner (Standard: aktueller Ordner)
          -n  Dateiname (Standard: vom Server bzw. aus der URL)
          -c  maximale Verbindungen (Standard: 8)
          -l  Geschwindigkeitslimit, z. B. 500K oder 2M (pro Sekunde)

        Strg+C pausiert. Derselbe Befehl setzt den Download fort.
        """);
    return 0;
}

if (!Uri.TryCreate(args[0], UriKind.Absolute, out var url) || !TunasDM.Core.DownloadSchemes.IsSupported(url))
{
    Console.Error.WriteLine($"Ungültige URL: {args[0]}");
    return 1;
}

string directory = Environment.CurrentDirectory;
string? fileName = null;
var options = new DownloadOptions();
SpeedLimiter? limiter = null;

for (int i = 1; i < args.Length; i++)
{
    string? value = i + 1 < args.Length ? args[i + 1] : null;
    switch (args[i])
    {
        case "-o" when value is not null:
            directory = Path.GetFullPath(value);
            i++;
            break;
        case "-n" when value is not null:
            fileName = value;
            i++;
            break;
        case "-c" when int.TryParse(value, out int connections) && connections is >= 1 and <= 32:
            options.MaxConnections = connections;
            i++;
            break;
        case "-l" when value is not null && ByteSize.TryParse(value, out long limit):
            limiter = new SpeedLimiter(limit);
            i++;
            break;
        default:
            Console.Error.WriteLine($"Unbekannte oder ungültige Option: {args[i]}");
            return 1;
    }
}

string? statePath = DownloadStateStore.FindByUrl(directory, url);
Download download;
if (statePath is not null)
{
    Console.WriteLine($"Setze fort: {Path.GetFileName(DownloadStateStore.Load(statePath).FilePath)}");
    download = Download.Resume(statePath, options, limiter: limiter);
}
else
{
    download = Download.Create(new DownloadRequest(url) { TargetDirectory = directory, FileName = fileName }, options, limiter: limiter);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var meter = new SpeedMeter();
var run = download.RunAsync(cts.Token);
var lastLine = DateTime.MinValue;
while (!run.IsCompleted)
{
    await Task.WhenAny(run, Task.Delay(250));
    var progress = download.GetProgress();
    double speed = meter.Update(progress.Downloaded);
    if (!Console.IsOutputRedirected)
        Console.Write("\r" + Fit(Render(progress, speed)));
    else if (DateTime.Now - lastLine > TimeSpan.FromSeconds(2))
    {
        Console.WriteLine(Render(progress, speed));
        lastLine = DateTime.Now;
    }
}

Console.WriteLine();
try
{
    await run;
    Console.WriteLine($"Fertig: {download.FilePath} ({ByteSize.Format(download.TotalSize)})");
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine(download.SupportsRanges
        ? "Pausiert. Zum Fortsetzen denselben Befehl erneut ausführen."
        : "Abgebrochen. Der Server unterstützt kein Fortsetzen.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    if (ex.InnerException is { } inner)
        Console.Error.WriteLine($"  Ursache: {inner.Message}");
    return 1;
}

static string Render(DownloadProgress p, double speed)
{
    string name = p.FileName ?? "…";
    string size = p.TotalSize >= 0
        ? $"{p.Downloaded * 100.0 / Math.Max(1, p.TotalSize),5:0.0} %  {ByteSize.Format(p.Downloaded)} / {ByteSize.Format(p.TotalSize)}"
        : ByteSize.Format(p.Downloaded);
    string eta = p.TotalSize > 0 && speed > 0 ? $"  noch {TimeSpan.FromSeconds((p.TotalSize - p.Downloaded) / speed):hh\\:mm\\:ss}" : "";
    return $"{name}  {size}  {ByteSize.Format(speed)}/s  {p.ActiveConnections} Verb.{eta}  {Bar(p, 24)}";
}

// Balken: █ geladen, ░ offen, je Zeichen ein Abschnitt der Datei.
static string Bar(DownloadProgress p, int width)
{
    if (p.TotalSize <= 0)
        return "";
    var cells = new char[width];
    for (int i = 0; i < width; i++)
    {
        long from = p.TotalSize * i / width, to = p.TotalSize * (i + 1) / width;
        long covered = p.Segments.Sum(s => Math.Max(0, Math.Min(s.Position, to) - Math.Max(s.Start, from)));
        cells[i] = covered * 2 >= to - from ? '█' : '░';
    }
    return "[" + new string(cells) + "]";
}

static string Fit(string line)
{
    int width = Math.Max(20, Console.WindowWidth - 1);
    return line.Length > width ? line[..width] : line.PadRight(width);
}
