using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

using TunasDM.Core;

namespace TunasDM.Core.Updates;

/// <summary>Neue Fassung auf GitHub: Version, Hinweise, Setup-Datei und deren SHA-256.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string? Notes, Uri SetupUrl, string SetupName, long Size, string? Sha256, Uri? Page);

/// <summary>
/// Sucht unter GitHub Releases (<c>releases/latest</c>) des öffentlichen Repositorys
/// nach einem neueren Setup
/// (<c>TUNAS-Download-Manager-Setup-x.y.z.exe</c>) und lädt es geprüft herunter.
/// Die Prüfsumme kommt aus GitHubs <c>digest</c> des Assets oder aus einer beiliegenden <c>.sha256</c>-Datei;
/// ohne Prüfsumme wird nichts installiert.
/// </summary>
public static class UpdateChecker
{
    public const string DefaultRepository = "tunahanakguen/tunas-download-manager-oss";
    public const string SetupPrefix = "TUNAS-Download-Manager-Setup-";

    public static Uri LatestReleaseUri(string repository) => new($"https://api.github.com/repos/{repository}/releases/latest");

    /// <summary>Neuere Fassung oder null (keine Veröffentlichung, nicht neuer, kein Setup darin).</summary>
    public static async Task<UpdateInfo?> CheckAsync(HttpClient client, Uri releaseApi, Version current, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // noch keine Veröffentlichung (oder Repository nicht öffentlich)
        if (!response.IsSuccessStatusCode)
            throw new DownloadException(L.F("Update-Server antwortet mit {0}.", (int)response.StatusCode));
        var release = await response.Content.ReadFromJsonAsync(UpdateJson.Default.Release, cancellationToken).ConfigureAwait(false);
        if (release is null || release.Draft || release.Prerelease || ParseVersion(release.TagName) is not { } version || version <= current)
            return null;

        var setup = release.Assets.FirstOrDefault(a =>
            a.Name.StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        if (setup is null || !Uri.TryCreate(setup.DownloadUrl, UriKind.Absolute, out var setupUrl) || setupUrl.Scheme != "https")
            return null;

        string? sha = setup.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? digest["sha256:".Length..].ToLowerInvariant()
            : null;
        if (sha is null && release.Assets.FirstOrDefault(a => a.Name.Equals(setup.Name + ".sha256", StringComparison.OrdinalIgnoreCase)) is { } sumAsset)
        {
            string sums = await client.GetStringAsync(sumAsset.DownloadUrl, cancellationToken).ConfigureAwait(false);
            sha = Media.ToolSetup.FindHash(sums, setup.Name) ?? (sums.Trim() is { Length: 64 } bare ? bare.ToLowerInvariant() : null);
        }
        Uri.TryCreate(release.HtmlUrl, UriKind.Absolute, out var page);
        return new UpdateInfo(version, release.TagName, release.Body, setupUrl, setup.Name, setup.Size, sha, page);
    }

    /// <summary>„v1.2.3“, „1.2“ oder „1.2.3-beta“ → Version (Zusätze nach „-“/„+“ fallen weg).</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        string text = tag.Trim().TrimStart('v', 'V');
        int cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
            text = text[..cut];
        if (!Version.TryParse(text, out var version))
            return null;
        // 1.2 und 1.2.0 sollen gleich sein.
        return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
    }

    /// <summary>Lädt das Setup in <paramref name="folder"/> und prüft es. Gibt den Pfad zurück.</summary>
    public static async Task<string> DownloadAsync(HttpClient client, UpdateInfo update, string folder,
        IProgress<(long Received, long Total)>? progress, CancellationToken cancellationToken)
    {
        if (update.Sha256 is not { Length: 64 } expected)
            throw new DownloadException(L.T("Für dieses Update ist keine Prüfsumme veröffentlicht."));
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(update.SetupName));
        string temp = target + ".download";
        try
        {
            using var response = await client.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DownloadException(L.F("Update-Download: Server antwortet mit {0}.", (int)response.StatusCode));
            long total = response.Content.Headers.ContentLength ?? update.Size;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    sha.AppendData(buffer, 0, read);
                    received += read;
                    progress?.Report((received, total));
                }
            }
            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != expected)
                throw new DownloadException(L.T("Prüfsumme des Updates stimmt nicht – Datei verworfen."));
            File.Move(temp, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

internal sealed record Release(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool Prerelease,
    [property: JsonPropertyName("assets")] List<ReleaseAsset> Assets);

internal sealed record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string DownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);

[JsonSerializable(typeof(Release))]
internal sealed partial class UpdateJson : JsonSerializerContext;
