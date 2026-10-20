using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

using TunasDM.Core;

namespace TunasDM.Core.Ftp;

/// <summary>Antwort des FTP-Servers: dreistelliger Code und Text (bei mehrzeiligen Antworten die letzte Zeile).</summary>
public sealed record FtpReply(int Code, string Text)
{
    public bool IsPositive => Code is >= 100 and < 400;
}

/// <summary>Fehler mit FTP-Antwort; 4xx gilt als vorübergehend (neuer Versuch), 5xx als endgültig.</summary>
public sealed class FtpException(FtpReply reply, string command)
    : DownloadException(L.F("FTP-Server: {0} {1} ({2})", reply.Code, reply.Text, command))
{
    public FtpReply Reply { get; } = reply;
    public bool IsTransient => Reply.Code is >= 400 and < 500;
}

/// <summary>
/// Schlanke FTP-Steuerverbindung für Downloads: Anmeldung, Binärmodus, SIZE, MDTM, REST und RETR über EPSV/PASV.
/// Eine Verbindung lädt einen Bereich; für mehrere Segmente öffnet der Download mehrere Verbindungen.
/// </summary>
public sealed partial class FtpConnection : IAsyncDisposable
{
    private readonly TcpClient _control;
    private Stream _stream;
    private StreamReader _reader;
    private readonly IPAddress _serverAddress;
    private readonly string _host;
    private bool _secure;
    private bool _disposed;

    private FtpConnection(TcpClient control, IPAddress serverAddress, string host)
    {
        _control = control;
        _stream = control.GetStream();
        _reader = CreateReader(_stream);
        _serverAddress = serverAddress;
        _host = host;
    }

    // Latin-1 bildet jedes Byte ab; UTF-8 wird nach „OPTS UTF8 ON“ trotzdem korrekt übertragen (Pfade gehen als UTF-8 raus).
    private static StreamReader CreateReader(Stream stream) => new(stream, Encoding.Latin1, false, 1024, leaveOpen: true);

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Nur für Tests: Zertifikatsprüfung ersetzen (selbst signierter Testserver).</summary>
    internal static RemoteCertificateValidationCallback? CertificateValidation { get; set; }

    /// <summary>Steuer- und Datenverbindung sind mit TLS verschlüsselt (ftps/ftpes).</summary>
    public bool IsSecure => _secure;

    /// <summary>
    /// Verbindet und meldet an (Benutzer aus der Adresse, sonst „anonymous“), Binärmodus.
    /// <c>ftps://</c>: TLS sofort (Port 990); <c>ftpes://</c>: AUTH TLS nach der Begrüßung (Port 21).
    /// </summary>
    public static async Task<FtpConnection> OpenAsync(Uri url, CancellationToken token)
    {
        if (!DownloadSchemes.IsFtp(url))
            throw new DownloadException(L.F("Keine FTP-Adresse: {0}", url));
        bool implicitTls = url.Scheme == "ftps";
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(DefaultTimeout);
                await client.ConnectAsync(url.IdnHost, url.Port > 0 ? url.Port : implicitTls ? 990 : 21, timeout.Token).ConfigureAwait(false);
            }
            var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
            var connection = new FtpConnection(client, remote, url.IdnHost);
            try
            {
                if (implicitTls)
                    await connection.StartTlsAsync(token).ConfigureAwait(false);
                await connection.ExpectAsync(await connection.ReadReplyAsync(token).ConfigureAwait(false), "Verbindung").ConfigureAwait(false);
                if (url.Scheme == "ftpes")
                {
                    var auth = await connection.CommandAsync("AUTH TLS", token).ConfigureAwait(false);
                    if (auth.Code != 234)
                        throw new FtpException(auth, "AUTH TLS");
                    await connection.StartTlsAsync(token).ConfigureAwait(false);
                }
                var (user, password) = Credentials(url);
                var reply = await connection.CommandAsync("USER " + user, token).ConfigureAwait(false);
                if (reply.Code == 331)
                    reply = await connection.CommandAsync("PASS " + password, token, logAs: "PASS ***").ConfigureAwait(false);
                await connection.ExpectAsync(reply, "Anmeldung").ConfigureAwait(false);
                if (connection._secure)
                {
                    // Datenverbindungen ebenfalls verschlüsseln (RFC 4217), wie FileZilla nach der Anmeldung.
                    await connection.ExpectAsync(await connection.CommandAsync("PBSZ 0", token).ConfigureAwait(false), "PBSZ").ConfigureAwait(false);
                    await connection.ExpectAsync(await connection.CommandAsync("PROT P", token).ConfigureAwait(false), "PROT P").ConfigureAwait(false);
                }
                await connection.CommandAsync("OPTS UTF8 ON", token).ConfigureAwait(false); // nicht jeder Server kann es
                await connection.ExpectAsync(await connection.CommandAsync("TYPE I", token).ConfigureAwait(false), "TYPE I").ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Steuerverbindung auf TLS umstellen; danach gelesene Antworten kommen verschlüsselt.</summary>
    private async Task StartTlsAsync(CancellationToken token)
    {
        var tls = await AuthenticateAsync(_stream, token).ConfigureAwait(false);
        _reader.Dispose();
        _stream = tls;
        _reader = CreateReader(tls);
        _secure = true;
    }

    /// <summary>TLS-Handshake als Client; Windows nimmt für die Datenverbindung die Sitzung der Steuerverbindung wieder auf.</summary>
    private async Task<SslStream> AuthenticateAsync(Stream inner, CancellationToken token)
    {
        var tls = new SslStream(inner, leaveInnerStreamOpen: false, CertificateValidation);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(DefaultTimeout);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, timeout.Token).ConfigureAwait(false);
            return tls;
        }
        catch (AuthenticationException ex)
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw new DownloadException(L.F("FTPS: Verschlüsselte Verbindung nicht möglich ({0})", ex.Message));
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Benutzer und Passwort aus <c>ftp://user:pass@host/</c>, sonst anonym.</summary>
    public static (string User, string Password) Credentials(Uri url)
    {
        if (string.IsNullOrEmpty(url.UserInfo))
            return ("anonymous", "anonymous@");
        string[] parts = url.UserInfo.Split(':', 2);
        return (Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
    }

    /// <summary>Pfad auf dem Server, unverändert aus der Adresse (z. B. „/pub/datei.iso“).</summary>
    public static string PathOf(Uri url) => Uri.UnescapeDataString(url.AbsolutePath);

    /// <summary>Größe in Bytes oder -1, wenn der Server SIZE nicht kennt.</summary>
    public async Task<long> SizeAsync(string path, CancellationToken token)
    {
        var reply = await CommandAsync("SIZE " + path, token).ConfigureAwait(false);
        if (reply.Code == 213 && long.TryParse(reply.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long size))
            return size;
        if (reply.Code == 550)
            throw new FtpException(reply, "SIZE");
        return -1;
    }

    /// <summary>Änderungszeit (UTC) oder null.</summary>
    public async Task<DateTimeOffset?> ModifiedAsync(string path, CancellationToken token)
    {
        var reply = await CommandAsync("MDTM " + path, token).ConfigureAwait(false);
        return reply.Code == 213 ? ParseMdtm(reply.Text) : null;
    }

    public static DateTimeOffset? ParseMdtm(string text) =>
        DateTime.TryParseExact(text.Trim().Split('.')[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? new DateTimeOffset(time, TimeSpan.Zero)
            : null;

    /// <summary>Kann der Server ab einer Stelle weiterladen (REST)?</summary>
    public async Task<bool> SupportsRestartAsync(CancellationToken token) =>
        (await CommandAsync("REST 0", token).ConfigureAwait(false)).Code == 350;

    /// <summary>
    /// Öffnet die Datenverbindung und startet RETR ab <paramref name="offset"/>. Der Stream gehört dem Aufrufer;
    /// sein Schließen beendet auch diese Steuerverbindung (vorzeitiges Ende eines Segments).
    /// </summary>
    public async Task<Stream> RetrieveAsync(string path, long offset, CancellationToken token)
    {
        var endpoint = await PassiveEndpointAsync(token).ConfigureAwait(false);
        var data = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(DefaultTimeout);
                await data.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
            }
            if (offset > 0)
            {
                var rest = await CommandAsync("REST " + offset.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
                if (rest.Code != 350)
                    throw new FtpException(rest, "REST");
            }
            // FTPS: der Handshake auf der Datenverbindung läuft parallel zu RETR – manche Server antworten erst danach mit 150.
            Task<SslStream>? tls = _secure ? AuthenticateAsync(data.GetStream(), token) : null;
            FtpReply reply;
            try
            {
                reply = await CommandAsync("RETR " + path, token).ConfigureAwait(false);
            }
            catch
            {
                if (tls is not null)
                    _ = tls.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw;
            }
            if (reply.Code is not (125 or 150))
            {
                data.Dispose();
                if (tls is not null)
                    _ = tls.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new FtpException(reply, "RETR");
            }
            Stream inner = tls is null ? data.GetStream() : await tls.ConfigureAwait(false);
            return new FtpDataStream(data, inner, this);
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    /// <summary>EPSV bevorzugt (IPv6, NAT), sonst PASV. Die Adresse aus PASV wird ignoriert: immer der Server selbst.</summary>
    private async Task<IPEndPoint> PassiveEndpointAsync(CancellationToken token)
    {
        var reply = await CommandAsync("EPSV", token).ConfigureAwait(false);
        if (reply.Code == 229 && EpsvPort().Match(reply.Text) is { Success: true } epsv)
            return new IPEndPoint(_serverAddress, int.Parse(epsv.Groups[1].Value, CultureInfo.InvariantCulture));

        reply = await CommandAsync("PASV", token).ConfigureAwait(false);
        if (reply.Code == 227 && PasvAddress().Match(reply.Text) is { Success: true } pasv)
        {
            int port = int.Parse(pasv.Groups[5].Value, CultureInfo.InvariantCulture) * 256 + int.Parse(pasv.Groups[6].Value, CultureInfo.InvariantCulture);
            return new IPEndPoint(_serverAddress, port);
        }
        throw new FtpException(reply, "PASV");
    }

    public async Task<FtpReply> CommandAsync(string command, CancellationToken token, string? logAs = null)
    {
        if (command.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new DownloadException(L.T("Ungültiges Zeichen im FTP-Pfad."));
        byte[] line = Encoding.UTF8.GetBytes(command + "\r\n");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(DefaultTimeout);
        try
        {
            await _stream.WriteAsync(line, timeout.Token).ConfigureAwait(false);
            return await ReadReplyAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(L.F("FTP-Server antwortet nicht ({0}).", logAs ?? command.Split(' ')[0]));
        }
    }

    /// <summary>Liest eine Antwort; mehrzeilig „123-…“ bis „123 …“.</summary>
    internal async Task<FtpReply> ReadReplyAsync(CancellationToken token)
    {
        string? first = await _reader.ReadLineAsync(token).ConfigureAwait(false)
            ?? throw new IOException(L.T("Der FTP-Server hat die Verbindung beendet."));
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            throw new IOException(L.F("Unverständliche FTP-Antwort: {0}", first));
        string last = first;
        if (first.Length > 3 && first[3] == '-')
        {
            string end = first[..3] + " ";
            while (!last.StartsWith(end, StringComparison.Ordinal))
                last = await _reader.ReadLineAsync(token).ConfigureAwait(false) ?? throw new IOException(L.T("Der FTP-Server hat die Verbindung beendet."));
        }
        return new FtpReply(code, last.Length > 4 ? last[4..] : "");
    }

    private Task ExpectAsync(FtpReply reply, string step) =>
        reply.IsPositive ? Task.CompletedTask : Task.FromException(new FtpException(reply, step));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _stream.WriteAsync("QUIT\r\n"u8.ToArray(), quit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        _reader.Dispose();
        _stream.Dispose();
        _control.Dispose();
    }

    [GeneratedRegex(@"\(\|\|\|(\d+)\|\)")]
    private static partial Regex EpsvPort();

    [GeneratedRegex(@"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)")]
    private static partial Regex PasvAddress();

    /// <summary>Datenverbindung; Schließen beendet auch die Steuerverbindung (Segment fertig oder abgebrochen).</summary>
    private sealed class FtpDataStream(TcpClient data, Stream inner, FtpConnection control) : Stream
    {
        private readonly Stream _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            _inner.Dispose();
            data.Dispose();
            await control.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                data.Dispose();
                control.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            base.Dispose(disposing);
        }
    }
}
