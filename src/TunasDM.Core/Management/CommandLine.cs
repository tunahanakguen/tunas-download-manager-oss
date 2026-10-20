namespace TunasDM.Core.Management;

/// <summary>Aufruf von außen, kompatibel zu IDM: <c>/d URL [/p Pfad] [/f Name] [/a] [/n] [/q]</c> und <c>/s</c>.</summary>
public sealed record CommandLineRequest
{
    public Uri? Url { get; init; }
    public string? Directory { get; init; }
    public string? FileName { get; init; }

    /// <summary>/a: nur in die Warteschlange, nicht starten.</summary>
    public bool AddOnly { get; init; }

    /// <summary>/n: keine Rückfragen (kein Datei-Info-Dialog).</summary>
    public bool Silent { get; init; }

    /// <summary>/q: nach erfolgreichem Download beenden.</summary>
    public bool QuitAfter { get; init; }

    /// <summary>/s: Warteschlange starten.</summary>
    public bool StartQueue { get; init; }

    /// <summary>/tray: nur im Infobereich starten (Autostart).</summary>
    public bool Minimized { get; init; }

    /// <summary>/exit: laufende Instanz sauber beenden (Installer, Deinstallation).</summary>
    public bool Exit { get; init; }

    /// <summary>/tools: yt-dlp und ffmpeg im Hintergrund einrichten (Installer).</summary>
    public bool SetupTools { get; init; }

    public bool IsEmpty => Url is null && !StartQueue && !Minimized && !Exit && !SetupTools;

    public static CommandLineRequest Parse(IReadOnlyList<string> args)
    {
        var request = new CommandLineRequest();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? next = i + 1 < args.Count ? args[i + 1] : null;
            switch (Normalize(arg))
            {
                case "d" when next is not null && TryUrl(next, out var url):
                    request = request with { Url = url };
                    i++;
                    break;
                case "p" when next is not null:
                    request = request with { Directory = next };
                    i++;
                    break;
                case "f" when next is not null:
                    request = request with { FileName = next };
                    i++;
                    break;
                case "a":
                    request = request with { AddOnly = true };
                    break;
                case "n":
                    request = request with { Silent = true };
                    break;
                case "q":
                    request = request with { QuitAfter = true };
                    break;
                case "s":
                    request = request with { StartQueue = true };
                    break;
                case "exit":
                    request = request with { Exit = true };
                    break;
                case "tools":
                    request = request with { SetupTools = true };
                    break;
                case "tray":
                    request = request with { Minimized = true };
                    break;
                case null when request.Url is null && TryUrl(arg, out var bare):
                    // Nackte URL, z. B. per „Öffnen mit“ oder Drag & Drop auf die Exe.
                    request = request with { Url = bare };
                    break;
            }
        }
        return request;
    }

    /// <summary>Wieder in Argumente zerlegen (zum Weiterreichen an die laufende Instanz).</summary>
    public IReadOnlyList<string> ToArguments()
    {
        var args = new List<string>();
        if (Url is not null)
            args.AddRange(["/d", Url.ToString()]);
        if (Directory is not null)
            args.AddRange(["/p", Directory]);
        if (FileName is not null)
            args.AddRange(["/f", FileName]);
        if (AddOnly)
            args.Add("/a");
        if (Silent)
            args.Add("/n");
        if (QuitAfter)
            args.Add("/q");
        if (StartQueue)
            args.Add("/s");
        if (Exit)
            args.Add("/exit");
        if (SetupTools)
            args.Add("/tools");
        if (Minimized)
            args.Add("/tray");
        return args;
    }

    private static string? Normalize(string arg) =>
        arg.Length > 1 && arg[0] is '/' or '-' && !arg.StartsWith("//", StringComparison.Ordinal)
            ? arg.TrimStart('/', '-').ToLowerInvariant()
            : null;

    private static bool TryUrl(string text, out Uri url) =>
        Uri.TryCreate(text.Trim().Trim('"'), UriKind.Absolute, out url!) && DownloadSchemes.IsSupported(url);
}
