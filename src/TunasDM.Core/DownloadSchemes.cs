namespace TunasDM.Core;

/// <summary>Adressarten, die TUNAS laden kann: HTTP(S) und FTP, auch verschlüsselt (FTPS).</summary>
public static class DownloadSchemes
{
    /// <summary>
    /// <c>ftp</c> unverschlüsselt, <c>ftpes</c> mit explizitem TLS (AUTH TLS auf Port 21),
    /// <c>ftps</c> mit implizitem TLS (Port 990).
    /// </summary>
    public static bool IsFtp(Uri url) => url.Scheme is "ftp" or "ftps" or "ftpes";

    public static bool IsSupported(Uri url) => url.Scheme is "http" or "https" || IsFtp(url);
}
