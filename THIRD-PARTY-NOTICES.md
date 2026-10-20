# Fremde Bausteine (Third-Party Notices)

TUNAS Download Manager steht unter der **GNU General Public License v3.0** (siehe [LICENSE](LICENSE)).
Er nutzt die folgenden Bausteine; alle sind mit der GPL-3.0 vereinbar.

## Im Setup enthalten

| Baustein | Lizenz | Quelle |
|---|---|---|
| .NET 10 Runtime, WPF, Windows Forms | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| WPF-UI 4.3 (Fluent-Oberfläche) | MIT | https://github.com/lepoco/wpfui |
| CommunityToolkit.Mvvm 8.4 | MIT | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Data.Sqlite 10 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (Anbindung an SQLite) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite | Public Domain | https://sqlite.org/copyright.html |
| System.Security.Cryptography.ProtectedData | MIT | https://github.com/dotnet/runtime |

## Nicht enthalten, auf Wunsch nachgeladen

Diese Programme lädt TUNAS erst, wenn die Video-Funktion eingerichtet wird (Setup-Option oder Optionen →
„Video-Werkzeuge einrichten“), direkt von den offiziellen Projektseiten und mit Prüfsumme. Sie laufen als
eigene Programme; TUNAS ruft sie nur auf.

| Programm | Lizenz | Quelle |
|---|---|---|
| yt-dlp | Unlicense | https://github.com/yt-dlp/yt-dlp |
| FFmpeg (Build „win64-gpl“ von yt-dlp/FFmpeg-Builds) | GPL-3.0 | https://github.com/yt-dlp/FFmpeg-Builds |

## Werkzeuge zum Bauen (nicht enthalten)

| Werkzeug | Lizenz |
|---|---|
| Inno Setup 6 (Installer) | Inno Setup License (frei, auch kommerziell) |
| xUnit, Microsoft.NET.Test.Sdk, coverlet (nur Tests) | Apache-2.0 / MIT |

## Kompatibilität

TUNAS liest und schreibt Download-Listen im Format `.ef2` und versteht die üblichen Kommandozeilen-Schalter
(`/d`, `/p`, `/f`, `/a`, `/n`, `/q`, `/s`), damit Listen und Skripte aus Internet Download Manager weiter
funktionieren. Der Code ist eigenständig geschrieben; er enthält keinen Code, keine Texte und keine Grafiken
von Internet Download Manager. „Internet Download Manager“ und „IDM“ sind Marken von Tonec Inc.; TUNAS steht in
keiner Verbindung zu Tonec.
