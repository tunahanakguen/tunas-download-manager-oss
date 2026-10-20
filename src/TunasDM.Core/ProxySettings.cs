using System.Net;

namespace TunasDM.Core;

public enum ProxyMode
{
    /// <summary>Wie Windows (Internetoptionen, auch automatische Konfiguration).</summary>
    System,

    /// <summary>Direkt, ohne Proxy.</summary>
    None,

    /// <summary>Eigener Proxy: http://, https:// oder socks5://.</summary>
    Manual,
}

/// <summary>Proxy für Downloads und yt-dlp.</summary>
public sealed record ProxySettings(ProxyMode Mode, string? Address = null, string? User = null, string? Password = null)
{
    /// <summary>
    /// Adresse mit Schema. „host:port“ ohne Schema gilt als HTTP-Proxy.
    /// Null bei Modus System/None oder ungültiger Eingabe.
    /// </summary>
    public Uri? ProxyUri
    {
        get
        {
            if (Mode != ProxyMode.Manual || string.IsNullOrWhiteSpace(Address))
                return null;
            string text = Address.Trim();
            if (!text.Contains("://", StringComparison.Ordinal))
                text = "http://" + text;
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "socks4" or "socks4a" or "socks5"
                && uri.Host.Length > 0 && uri.Port > 0 ? uri : null;
        }
    }

    public void ApplyTo(System.Net.Http.SocketsHttpHandler handler)
    {
        switch (Mode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Manual when ProxyUri is { } uri:
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(uri)
                {
                    BypassProxyOnLocal = true,
                    Credentials = string.IsNullOrEmpty(User) ? null : new NetworkCredential(User, Password),
                };
                break;
            default:
                // System: SocketsHttpHandler nutzt von sich aus die Windows-Einstellungen.
                break;
        }
    }

    /// <summary>Argument für yt-dlp <c>--proxy</c>: leer heißt direkt, null heißt Systemvorgabe.</summary>
    public string? YtDlpArgument()
    {
        if (Mode == ProxyMode.None)
            return "";
        if (ProxyUri is not { } uri)
            return null;
        if (string.IsNullOrEmpty(User))
            return uri.ToString().TrimEnd('/');
        var builder = new UriBuilder(uri) { UserName = Uri.EscapeDataString(User), Password = Uri.EscapeDataString(Password ?? "") };
        return builder.Uri.ToString().TrimEnd('/');
    }
}
