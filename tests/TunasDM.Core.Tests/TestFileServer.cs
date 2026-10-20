using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TunasDM.Core.Tests;

/// <summary>Lokaler HTTP-Server, der eine erzeugte Datei mit einstellbarem Verhalten ausliefert.</summary>
public sealed class TestFileServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _active;
    private int _maxActive;
    private int _requests;
    private int _dropsLeft;

    private TestFileServer(WebApplication app) => _app = app;

    public long Size { get; set; }
    public bool SupportsRanges { get; set; } = true;
    public bool SendContentLength { get; set; } = true;

    /// <summary>Andere Version = anderer Inhalt und anderes ETag.</summary>
    public string Version { get; set; } = "v1";

    public int ChunkDelayMs { get; set; }

    /// <summary>Diese Anfragen (1 = erste) werden stark gedrosselt.</summary>
    public HashSet<int> SlowRequests { get; } = new();

    public int SlowChunkDelayMs { get; set; } = 300;
    public string? ContentDisposition { get; set; }
    public int DropAfterBytes { get; set; }
    public ConcurrentQueue<string> RangeHeaders { get; } = new();
    public Uri FileUri { get; private set; } = null!;

    /// <summary>Pfad und Cookie-Header jeder Anfrage (leer = kein Cookie).</summary>
    public ConcurrentQueue<(string Path, string Cookie)> CookieHeaders { get; } = new();

    public Uri? RedirectTarget { get; set; }

    /// <summary>Gesetzt: jede Anfrage (außer /redirect) bekommt nur diesen Status, z. B. 403 für abgelaufene Links.</summary>
    public int? StatusOverride { get; set; }

    public Uri RedirectUri => new(FileUri, "/redirect");
    public int MaxConcurrent => Volatile.Read(ref _maxActive);
    public int RequestCount => Volatile.Read(ref _requests);

    /// <summary>Die nächsten <paramref name="count"/> Antworten brechen nach DropAfterBytes ab.</summary>
    public void DropNext(int count, int afterBytes)
    {
        DropAfterBytes = afterBytes;
        Volatile.Write(ref _dropsLeft, count);
    }

    public static byte ByteAt(long offset, string version)
    {
        ulong seed = version == "v1" ? 17UL : 91UL;
        return (byte)((((ulong)offset * 2654435761UL) >> 13) ^ seed);
    }

    public byte[] Expected(string? version = null)
    {
        var data = new byte[Size];
        for (long i = 0; i < data.Length; i++)
            data[i] = ByteAt(i, version ?? Version);
        return data;
    }

    public static async Task<TestFileServer> StartAsync(long size, int port = 0)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        var server = new TestFileServer(app) { Size = size };
        app.Run(server.HandleAsync);
        await app.StartAsync();

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.FileUri = new Uri(new Uri(address), "files/test.bin");
        return server;
    }

    private async Task HandleAsync(HttpContext context)
    {
        int number = Interlocked.Increment(ref _requests);
        context.Items["number"] = number;
        int now = Interlocked.Increment(ref _active);
        int max;
        while (now > (max = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, now, max) != max)
        {
        }

        try
        {
            await ServeAsync(context);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Client hat abgebrochen.
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task ServeAsync(HttpContext context)
    {
        var request = context.Request;
        var response = context.Response;
        CookieHeaders.Enqueue((request.Path + request.QueryString, request.Headers.Cookie.ToString()));

        // /redirect → Ziel aus RedirectTarget (z. B. anderer Host für den Cookie-Test).
        if (request.Path == "/redirect" && RedirectTarget is { } target)
        {
            response.StatusCode = StatusCodes.Status302Found;
            response.Headers.Location = target.ToString();
            return;
        }
        if (StatusOverride is int status)
        {
            response.StatusCode = status;
            return;
        }
        string version = Version;
        string etag = $"\"{version}\"";
        long from = 0, to = Size - 1;
        bool partial = false;

        string range = request.Headers.Range.ToString();
        if (range.Length > 0)
            RangeHeaders.Enqueue(range);

        if (SupportsRanges)
        {
            response.Headers.AcceptRanges = "bytes";
            response.Headers.ETag = etag;
            string ifRange = request.Headers.IfRange.ToString();
            bool conditionHolds = ifRange.Length == 0 || ifRange == etag;
            if (range.Length > 0 && conditionHolds && TryParseRange(range, out from, out to))
                partial = true;
            else
                (from, to) = (0, Size - 1);
        }

        if (ContentDisposition is not null)
            response.Headers.ContentDisposition = ContentDisposition;
        response.ContentType = "application/octet-stream";
        if (partial)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {from}-{to}/{Size}";
        }
        if (SendContentLength)
            response.ContentLength = to - from + 1;

        bool drop = DropAfterBytes > 0 && Interlocked.Decrement(ref _dropsLeft) >= 0;
        int delay = SlowRequests.Contains((int)context.Items["number"]!) ? SlowChunkDelayMs : ChunkDelayMs;
        var buffer = new byte[16 * 1024];
        long sent = 0;
        for (long position = from; position <= to;)
        {
            int count = (int)Math.Min(buffer.Length, to - position + 1);
            for (int i = 0; i < count; i++)
                buffer[i] = ByteAt(position + i, version);

            if (drop && sent + count > DropAfterBytes)
            {
                int rest = (int)(DropAfterBytes - sent);
                await response.Body.WriteAsync(buffer.AsMemory(0, rest), context.RequestAborted);
                await response.Body.FlushAsync(context.RequestAborted);
                context.Abort();
                return;
            }

            await response.Body.WriteAsync(buffer.AsMemory(0, count), context.RequestAborted);
            sent += count;
            position += count;
            if (delay > 0)
                await Task.Delay(delay, context.RequestAborted);
        }
    }

    private bool TryParseRange(string header, out long from, out long to)
    {
        from = 0;
        to = Size - 1;
        if (!header.StartsWith("bytes=", StringComparison.Ordinal))
            return false;
        var parts = header["bytes=".Length..].Split('-');
        if (parts.Length != 2 || !long.TryParse(parts[0], out from) || from >= Size)
            return false;
        if (parts[1].Length > 0 && long.TryParse(parts[1], out long end))
            to = Math.Min(end, Size - 1);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
