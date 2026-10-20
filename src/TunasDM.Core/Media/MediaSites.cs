namespace TunasDM.Core.Media;

/// <summary>Erkennt Adressen, die über yt-dlp statt als Datei geladen werden sollten.</summary>
public static class MediaSites
{
    private static readonly string[] Hosts =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "vimeo.com", "dailymotion.com", "twitch.tv",
        "tiktok.com", "instagram.com", "facebook.com", "x.com", "twitter.com", "soundcloud.com",
        "bilibili.com", "rumble.com", "ardmediathek.de", "zdf.de", "arte.tv", "3sat.de",
    ];

    /// <summary>Großes Portal mit eigenem yt-dlp-Extraktor (YouTube & Co.): Titel und Formate von dort sind verlässlich.</summary>
    public static bool IsKnownSite(Uri url)
    {
        string host = url.Host.ToLowerInvariant();
        return Hosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    public static bool IsMediaPage(Uri url)
    {
        if (IsKnownSite(url))
            return true;
        string path = url.AbsolutePath.ToLowerInvariant();
        return path.EndsWith(".m3u8", StringComparison.Ordinal) || path.EndsWith(".mpd", StringComparison.Ordinal);
    }
}
