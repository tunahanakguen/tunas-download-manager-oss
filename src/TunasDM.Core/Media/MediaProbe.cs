namespace TunasDM.Core.Media;

/// <summary>Ergebnis der Abfrage: welche Adresse geladen wird und was sie anbietet.</summary>
public sealed record MediaChoice(Uri Url, MediaInfo Info);

/// <summary>
/// Fragt alle Kandidaten (direkte Quelle, Player-Frame, Seite, erkannte HLS-/DASH-Listen) gleichzeitig ab
/// und wählt die beste Fassung: Bei großen Portalen gilt deren Seite, sonst die höchste Auflösung.
/// Nach dem ersten Treffer wird nur noch kurz auf langsamere Kandidaten gewartet.
/// </summary>
public static class MediaProbe
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(4);

    public static async Task<MediaChoice?> ProbeBestAsync(IReadOnlyList<Uri> candidates,
        Func<Uri, CancellationToken, Task<MediaInfo>> probe, TimeSpan grace, CancellationToken cancellationToken,
        Action<string>? onError = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = candidates.Select((url, index) => (Url: url, Index: index, Task: probe(url, cts.Token))).ToList();
        var found = new List<(Uri Url, int Index, MediaInfo Info)>();
        Task? graceTimer = null;

        try
        {
            while (pending.Count > 0)
            {
                var waitFor = pending.Select(p => p.Task).Cast<Task>().ToList();
                if (graceTimer is not null)
                    waitFor.Add(graceTimer);
                var done = await Task.WhenAny(waitFor).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (done == graceTimer)
                    break;

                var entry = pending.First(p => p.Task == done);
                pending.Remove(entry);
                try
                {
                    var info = await entry.Task.ConfigureAwait(false);
                    if (info.Options.Count == 0)
                        continue;
                    found.Add((entry.Url, entry.Index, info));
                    // Großes Portal (eigener Extraktor): sofort fertig, besser wird es nicht.
                    if (MediaSites.IsKnownSite(entry.Url))
                        break;
                    graceTimer ??= Task.Delay(grace, cts.Token);
                }
                catch (DownloadException ex)
                {
                    onError?.Invoke(ex.Message);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }
        }
        finally
        {
            cts.Cancel(); // übrige yt-dlp-Prozesse beenden
        }

        if (found.Count == 0)
            return null;
        var best = found
            .OrderByDescending(f => MediaSites.IsKnownSite(f.Url))
            .ThenByDescending(f => MaxHeight(f.Info))
            .ThenBy(f => f.Index)
            .First();
        return new MediaChoice(best.Url, best.Info);
    }

    /// <summary>Höchste angebotene Auflösung (0 = unbekannt, z. B. nur „Beste Qualität“ oder Audio).</summary>
    public static int MaxHeight(MediaInfo info) =>
        info.Options.Select(o => o.Code.StartsWith('v') && int.TryParse(o.Code.AsSpan(1), out int h) ? h : 0).DefaultIfEmpty(0).Max();

    /// <summary>
    /// Dateiname: Bei großen Portalen der Titel von yt-dlp, sonst der Titel der Seite (yt-dlp rät bei
    /// fremden Seiten oft nur aus dem Dateinamen, z. B. „228747261_240p“). Benachrichtigungszähler „(3) “ fällt weg.
    /// </summary>
    public static string ChooseTitle(MediaChoice choice, string? pageTitle)
    {
        string? page = CleanPageTitle(pageTitle);
        if (MediaSites.IsKnownSite(choice.Url) || page is null)
            return choice.Info.Title;
        return page;
    }

    public static string? CleanPageTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        string text = System.Text.RegularExpressions.Regex.Replace(title.Trim(), @"^\(\d+\)\s*", "");
        return text.Length == 0 ? null : text;
    }
}
