using System.Security.Cryptography;
using System.Text;

namespace TunasDM.Core;

/// <summary>
/// Cookies und Passwörter nur verschlüsselt ablegen (Windows DPAPI, an den Benutzer gebunden).
/// Alte Klartext-Werte ohne Präfix werden weiter gelesen und beim nächsten Speichern verschlüsselt.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "TUNAS Download Manager"u8.ToArray();

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain) || plain.StartsWith(Prefix, StringComparison.Ordinal) || !OperatingSystem.IsWindows())
            return plain;
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    /// <summary>Adresse mit Passwort (z. B. <c>ftp://user:pass@host/</c>) nur verschlüsselt ablegen, andere unverändert.</summary>
    public static string ProtectUrl(string url) => HasPassword(url) ? Protect(url)! : url;

    /// <summary>Gegenstück zu <see cref="ProtectUrl"/>; nicht entschlüsselbar = leere Adresse.</summary>
    public static string UnprotectUrl(string stored) => Unprotect(stored) ?? "";

    public static bool HasPassword(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.IndexOf(':') is int colon and >= 0 && colon < uri.UserInfo.Length - 1;

    /// <summary>Zum Anzeigen: Passwort durch *** ersetzt.</summary>
    public static string MaskPassword(string url) => ReplacePassword(url, "***");

    /// <summary>Zum Weitergeben (Export): ohne Passwort, der Benutzername bleibt.</summary>
    public static string WithoutPassword(string url) => ReplacePassword(url, null);

    private static string ReplacePassword(string url, string? replacement)
    {
        if (!HasPassword(url))
            return url;
        var uri = new Uri(url);
        string user = uri.UserInfo.Split(':', 2)[0];
        string info = replacement is null ? user : user + ":" + replacement;
        return uri.Scheme + "://" + info + "@" + url[(url.IndexOf('@', uri.Scheme.Length + 3) + 1)..];
    }

    /// <summary>Gibt null zurück, wenn der Wert nicht entschlüsselt werden kann (anderer Benutzer, beschädigt).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal))
            return stored;
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
