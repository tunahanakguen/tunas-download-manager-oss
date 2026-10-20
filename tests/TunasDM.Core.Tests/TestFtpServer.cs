using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TunasDM.Core.Tests;

/// <summary>
/// Kleiner FTP-Server für Tests: eine Datei mit vorhersagbarem Inhalt (wie <see cref="TestFileServer"/>),
/// USER/PASS, TYPE, SIZE, MDTM, REST, EPSV/PASV, RETR. Einstellbar: kein REST, Verbindungslimit, Anmeldung, TLS.
/// </summary>
public sealed class TestFtpServer : IAsyncDisposable
{
    public enum TlsMode { None, Explicit, Implicit }

    private static readonly Lazy<X509Certificate2> Certificate = new(CreateCertificate);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private int _sessions;
    private int _maxSessions;

    public long Size { get; set; }
    public string Path { get; set; } = "/pub/test.bin";
    public string Version { get; set; } = "v1";
    public bool SupportsRest { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public DateTime Modified { get; set; } = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    public TlsMode Tls { get; set; }

    /// <summary>Datenverbindungen ohne PROT P abweisen (wie vsftpd mit force_local_data_ssl).</summary>
    public bool RequireProtectedData { get; set; } = true;

    /// <summary>Mehr gleichzeitige Sitzungen werden mit 421 abgewiesen (0 = unbegrenzt).</summary>
    public int SessionLimit { get; set; }

    /// <summary>Verzögerung pro 64-KB-Block, damit mehrere Verbindungen zum Zug kommen.</summary>
    public int ChunkDelayMs { get; set; }

    public int MaxConcurrent => Volatile.Read(ref _maxSessions);
    public ConcurrentQueue<long> RestOffsets { get; } = new();
    public ConcurrentQueue<string> Commands { get; } = new();

    public Uri FileUri => new($"{Tls switch { TlsMode.Explicit => "ftpes", TlsMode.Implicit => "ftps", _ => "ftp" }}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{Path}");

    public static TestFtpServer Start(long size, TlsMode tls = TlsMode.None)
    {
        var server = new TestFtpServer { Size = size, Tls = tls };
        server._listener.Start();
        _ = server.AcceptLoopAsync();
        return server;
    }

    public byte[] Expected() => Enumerable.Range(0, (int)Size).Select(i => TestFileServer.ByteAt(i, Version)).ToArray();

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch
            {
                return;
            }
            _ = Task.Run(() => SessionAsync(client));
        }
    }

    private async Task SessionAsync(TcpClient client)
    {
        int active = Interlocked.Increment(ref _sessions);
        int seen;
        while (active > (seen = Volatile.Read(ref _maxSessions)) && Interlocked.CompareExchange(ref _maxSessions, active, seen) != seen) { }
        using var _ = client;
        Stream stream = client.GetStream();
        StreamReader reader = null!;
        async Task Reply(string line) => await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"));
        TcpListener? passive = null;
        long rest = 0;
        string? user = null;
        bool loggedIn = false;
        bool secure = false, protectedData = false;
        try
        {
            if (Tls == TlsMode.Implicit)
            {
                stream = await ServerTlsAsync(stream);
                secure = true;
            }
            reader = new StreamReader(stream, Encoding.UTF8);
            if (SessionLimit > 0 && active > SessionLimit)
            {
                await Reply("421 Too many connections");
                return;
            }
            await Reply("220-TUNAS Test-FTP");
            await Reply("220 bereit");
            while (await reader.ReadLineAsync() is { } line)
            {
                Commands.Enqueue(line.StartsWith("PASS") ? "PASS ***" : line);
                string verb = line.Split(' ')[0].ToUpperInvariant();
                string arg = line.Length > verb.Length ? line[(verb.Length + 1)..] : "";
                if (!loggedIn && verb is not ("USER" or "PASS" or "QUIT" or "AUTH"))
                {
                    await Reply("530 Please login");
                    continue;
                }
                switch (verb)
                {
                    case "AUTH" when Tls == TlsMode.Explicit && !secure:
                        await Reply("234 AUTH TLS OK");
                        stream = await ServerTlsAsync(stream);
                        reader = new StreamReader(stream, Encoding.UTF8);
                        secure = true;
                        break;
                    case "USER" when Tls != TlsMode.None && !secure:
                        await Reply("530 TLS required");
                        break;
                    case "PBSZ" when secure:
                        await Reply("200 PBSZ=0");
                        break;
                    case "PROT" when secure:
                        protectedData = arg == "P";
                        await Reply("200 PROT now " + arg);
                        break;
                    case "USER":
                        user = arg;
                        await Reply("331 Password required");
                        break;
                    case "PASS":
                        loggedIn = User is null ? user == "anonymous" : user == User && arg == Password;
                        await Reply(loggedIn ? "230 Logged in" : "530 Login incorrect");
                        break;
                    case "OPTS":
                    case "TYPE":
                        await Reply("200 OK");
                        break;
                    case "SIZE":
                        await Reply(arg == Path ? $"213 {Size}" : "550 No such file");
                        break;
                    case "MDTM":
                        await Reply(arg == Path ? $"213 {Modified:yyyyMMddHHmmss}" : "550 No such file");
                        break;
                    case "REST" when SupportsRest:
                        rest = long.Parse(arg, CultureInfo.InvariantCulture);
                        await Reply($"350 Restarting at {rest}");
                        break;
                    case "REST":
                        await Reply("502 Command not implemented");
                        break;
                    case "EPSV":
                        passive?.Stop();
                        passive = new TcpListener(IPAddress.Loopback, 0);
                        passive.Start();
                        await Reply($"229 Entering Extended Passive Mode (|||{((IPEndPoint)passive.LocalEndpoint).Port}|)");
                        break;
                    case "RETR" when arg != Path:
                        await Reply("550 No such file");
                        break;
                    case "RETR" when Tls != TlsMode.None && RequireProtectedData && !protectedData:
                        await Reply("522 Data connections must be encrypted");
                        break;
                    case "RETR" when passive is not null:
                        RestOffsets.Enqueue(rest);
                        using (var data = await passive.AcceptTcpClientAsync())
                        {
                            await Reply("150 Opening BINARY mode data connection");
                            await using Stream target = protectedData ? await ServerTlsAsync(data.GetStream()) : data.GetStream();
                            await SendAsync(target, rest);
                        }
                        passive.Stop();
                        passive = null;
                        rest = 0;
                        await Reply("226 Transfer complete");
                        break;
                    case "QUIT":
                        await Reply("221 Bye");
                        return;
                    default:
                        await Reply("502 Command not implemented");
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // Client hat die Datenverbindung vorzeitig geschlossen (Segment fertig).
        }
        finally
        {
            passive?.Stop();
            Interlocked.Decrement(ref _sessions);
        }
    }

    private static async Task<Stream> ServerTlsAsync(Stream inner)
    {
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);
        await tls.AuthenticateAsServerAsync(Certificate.Value);
        return tls;
    }

    /// <summary>Selbst signiert für 127.0.0.1; über PKCS#12 geladen, damit Windows (SChannel) den Schlüssel nutzen kann.</summary>
    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private async Task SendAsync(Stream data, long from)
    {
        var buffer = new byte[64 * 1024];
        for (long position = from; position < Size;)
        {
            int count = (int)Math.Min(buffer.Length, Size - position);
            for (int i = 0; i < count; i++)
                buffer[i] = TestFileServer.ByteAt(position + i, Version);
            await data.WriteAsync(buffer.AsMemory(0, count));
            position += count;
            if (ChunkDelayMs > 0)
                await Task.Delay(ChunkDelayMs);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        return ValueTask.CompletedTask;
    }
}
