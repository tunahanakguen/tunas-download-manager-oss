# TUNAS Download Manager

![TUNAS](branding/TunasLogo.png)

Freier Download-Manager für Windows (GPL-3.0): segmentierte, wiederaufnehmbare Downloads, Warteschlangen,
Browser-Integration, Videos, FTP/FTPS, Site-Grabber.

## Herunterladen

**[Neueste Fassung unter Releases](https://github.com/tunahanakguen/tunas-download-manager-oss/releases/latest)**:
`TUNAS-Download-Manager-Setup-<Version>.exe` laden und ausführen (Windows 10/11, 64 Bit; .NET ist enthalten).
Das Setup ist noch nicht signiert: Meldet Windows SmartScreen „Der Computer wurde durch Windows geschützt“,
auf „Weitere Informationen“ → „Trotzdem ausführen“ klicken. Die Browser-Erweiterung wird danach als „entpackte
Erweiterung“ geladen; die Abschlussseite des Setups zeigt den Weg.

- [KONZEPT.md](KONZEPT.md): Architektur und Phasenplan
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md): verwendete Bausteine und ihre Lizenzen
- [TRADEMARKS.md](TRADEMARKS.md): Logo und Name „TUNAS“
- [CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md) und [PRIVACY.md](PRIVACY.md)

## Stand: Phase 1 (Download-Engine)

- Mehrere Verbindungen pro Datei mit dynamischer Segmentierung: Eine freie Verbindung teilt das größte
  offene Segment in der Mitte.
- Direktes Schreiben in die vorab angelegte Datei (`name.ext.tunasdm`), Stand in `name.ext.tunasdm.json`.
- Fortsetzen nach Pause oder Absturz, mit `If-Range` und Abgleich der letzten 4 KB.
- Erkennt geänderte Dateien auf dem Server; Wiederholungen bei Abbrüchen; Geschwindigkeitslimit.
- Bricht Verbindungen ab, die deutlich langsamer sind als die übrigen, und verteilt ihren Rest neu.

## Stand: Phase 2 (Oberfläche)

- WPF-App `TunasDM.exe` im TUNAS-Design: Download-Liste, Filter (Alle/Unfertig/Fertig/Warteschlange),
  Kategorien mit eigenen Zielordnern, Segmentbalken, Taskleisten-Fortschritt.
- Dialoge: URL hinzufügen, Download-Info (fragt Name und Größe vorab ab), Fortschritt mit Verbindungstabelle,
  Download abgeschlossen, Optionen.
- Warteschlange mit Limit gleichzeitiger Downloads, globales Geschwindigkeitslimit.
- Liste und Einstellungen in SQLite (`%LocalAppData%\TUNAS Download Manager\tunasdm.db`).
- Infobereich, Autostart, Zwischenablage-Überwachung, Drag & Drop von Links, nur eine Instanz.
- Kommandozeile: `TunasDM.exe /d URL [/p Ordner] [/f Name] [/a] [/n] [/q]`, `/s` startet die Warteschlange, `/exit` beendet die laufende App, `/tools` richtet yt-dlp und ffmpeg ein.

## Stand: Phase 3 (Browser-Integration)

- Erweiterung `extension/` (Manifest V3) für Chrome und Edge: übernimmt Downloads mit bekannten
  Dateitypen, Kontextmenü „Mit TUNAS laden“, „Alle Links mit TUNAS laden“ und „Website mit TUNAS durchsuchen“
  (öffnet den Site-Grabber mit der Seite), Symbol schaltet die Übernahme ein/aus. Texte auf Deutsch und Englisch
  (`extension/_locales`), je nach Sprache des Browsers.
- `TunasDM.Host.exe` (Native Messaging `com.tunas.dm`) verbindet Browser und App über die Named Pipe
  `TunasDM.Browser.<Sitzung>`, prüft, dass am anderen Ende wirklich `TunasDM.exe` läuft, und startet die App bei Bedarf.
- Die App meldet den Host bei jedem Start für den aktuellen Benutzer bei Chrome und Edge an. Angemeldet wird eine
  Kopie unter `%LocalAppData%\TUNAS Download Manager\host` (mit `TunasDM.app.txt` als Verweis auf die App):
  Ein Update oder Neubau entfernt so nie die Datei, die der Browser startet. Chrome merkt sich ein
  „Specified native messaging host not found“ sonst bis zum nächsten Neustart.

**Firefox** (ab 128): eigener Ordner `browser\firefox` mit Firefox-Manifest (`manifest.firefox.json` im Quellcode).
`about:debugging#/runtime/this-firefox` → „Temporäres Add-on laden“ → `manifest.json` wählen. Nicht signierte
Erweiterungen hält Firefox nur bis zum Neustart; dauerhaft erst mit Mozilla-Signatur. Der Host meldet sich unter
`HKCU\Software\Mozilla\NativeMessagingHosts` mit eigenem Manifest an (`allowed_extensions`).
Firefox aus dem Microsoft Store (MSIX) geht nicht: Er sieht nur einen eingefrorenen Stand von HKCU und `%LocalAppData%`
(geprüft per Marionette – neue Schlüssel und Ordner sind für ihn unsichtbar, auch die von Hand angelegten).
Firefox von mozilla.org verwenden.

**Erweiterung laden** (nach der Installation zeigt `extension.html` im Programmordner den Weg): `chrome://extensions` bzw. `edge://extensions` öffnen, Entwicklermodus an,
„Entpackte Erweiterung laden“ und den Ordner `browser\extension` neben `TunasDM.exe` wählen
(Optionen → Browser zeigt den Pfad).

**Nach einem Update der Erweiterung** dort einmal „Neu laden“ (Edge: „Erneut laden“) klicken. Ein Browser-Neustart
reicht nicht: Chrome und Edge behalten sonst das alte Hintergrundskript, das neue Seitenskript bekommt keine Antwort,
und z. B. das Feld „Video laden“ erscheint nicht. Ob alles läuft, zeigt das App-Protokoll (`log.txt`) mit Zeilen wie
`diag – www.youtube.com: Skript aktiv, 1 Video(s)` und `Symbol gezeigt`.

**Meldet die Erweiterung „Specified native messaging host not found“**, den Browser ganz beenden
(auch den Hintergrundprozess von „Startup Boost“ im Infobereich) und neu starten. Ein bereits laufender Edge
übernimmt eine neue Host-Anmeldung sonst nicht. Diagnose: `%LocalAppData%\TUNAS Download Manager\host.log`
(Host) und `log.txt` (App).

## Stand: Phase 4 (Videos)

- Im Browser erscheint oben rechts an Videos ein „Video laden“-Feld (auch in eingebetteten Playern und im Vollbild).
- Ein Klick öffnet „Video laden“ mit Vorschaubild, Titel, Dauer und den Qualitäten (z. B. 1080p, 720p,
  Nur Audio M4A/MP3) samt geschätzter Größe; die zuletzt gewählte Qualität ist vorausgewählt.
- Geladen wird über **yt-dlp**, Bild und Ton fügt **ffmpeg** zu MP4 (H.264/AAC bevorzugt) zusammen.
  HLS-/DASH-Streams anderer Seiten erkennt die Erweiterung im Netzwerkverkehr.
- „URL hinzufügen“ mit einer YouTube-/Vimeo-Adresse oder einer `.m3u8`/`.mpd`-Liste öffnet ebenfalls „Video laden“.
- Werkzeuge liegen in `%LocalAppData%\TUNAS Download Manager\tools` (`yt-dlp.exe`, `ffmpeg.exe`, `ffprobe.exe`);
  Optionen → Video zeigt die Versionen und aktualisiert yt-dlp.
- Jeder gestartete Download öffnet sein Fortschrittsfenster (abschaltbar unter Optionen → Allgemein).

## Stand: Phase 5 (Planen)

- **Zeitplaner** (Symbolleiste oder Tray): beliebig viele Warteschlangen neben der Hauptwarteschlange,
  je mit Zeitplan (einmal, täglich oder an Wochentagen), Stoppzeit, „Gleichzeitig laden“ und Wiederholungen bei Fehlern.
- **Wenn fertig:** nichts, TUNAS beenden, Energiesparmodus, Ruhezustand oder herunterfahren – mit einer Minute
  Countdown zum Abbrechen. Nur wenn in diesem Lauf wirklich etwas geladen wurde.
- Zur Stoppzeit angehaltene Downloads warten wieder in ihrer Warteschlange. Verpasste Startzeiten (TUNAS war zu)
  werden nicht nachgeholt.
- Rechtsklick → „In Warteschlange ▸“ ordnet Downloads zu; der Baum links zeigt jede Warteschlange.
  „Queue starten“ startet die im Baum gewählte, sonst die Hauptwarteschlange. Fertige Dateien aus einer
  Warteschlange öffnen kein Fenster je Datei.
- „Später laden“ (Download-Info und Video) fragt bei mehreren Warteschlangen, in welche.
- **Sync-Warteschlangen:** Bei jedem Start prüfen sie ihre fertigen Dateien auf dem Server (ETag, Änderungsdatum,
  Größe; `If-None-Match`/`If-Modified-Since`) und laden geänderte neu. Die alte Fassung wird erst ersetzt,
  wenn die neue vollständig ist – unter demselben Namen. Mit einem täglichen Zeitplan bleiben Dateien so aktuell.

## Stand: Phase 6 (Installer)

- **Setup** `TUNAS-Download-Manager-Setup-<Version>.exe` (~58 MB) mit TUNAS-Logo, mit Administratorrechten:
  installiert für alle Benutzer nach `C:\Program Files\TUNAS Download Manager` (Ordner beim ersten Installieren
  änderbar). Eine ältere Fassung unter `%LocalAppData%\Programs` (bis 0.7.0) wird dabei leise entfernt; Liste und
  Einstellungen bleiben.
  Bringt die .NET-Laufzeit mit, läuft also auf einem frischen Windows 10/11 (x64).
- Startmenü, optional Desktop-Symbol und Autostart. Der erste Start im Infobereich meldet den Browser-Host bei
  Chrome und Edge an; auf Wunsch lädt TUNAS dabei **yt-dlp und ffmpeg** im Hintergrund (etwa 220 MB, mit Prüfsumme).
  Ohne diese Option bietet „Video laden“ bzw. Optionen → Video „Einrichten“ an.
- Die Abschlussseite öffnet auf Wunsch `extension.html`: Anleitung zum Laden der Erweiterung mit dem Ordnerpfad.
- **Update:** einfach das neue Setup ausführen. Eine laufende App wird vorher sauber beendet (`TunasDM.exe /exit`),
  Downloads machen danach dort weiter, wo sie standen.
- **Deinstallieren** (Windows-Einstellungen → Apps) entfernt Programm, Autostart, Browser-Anmeldung und Host-Kopie
  und fragt, ob auch Liste, Einstellungen und Video-Werkzeuge weg sollen. Heruntergeladene Dateien bleiben immer.

- **Updates:** Die installierte Fassung sucht eine halbe Minute nach dem Start (höchstens einmal am Tag) unter
  GitHub Releases nach einem neueren Setup; von Hand über Infobereich → „Nach Updates suchen“. Das Fenster zeigt die
  Hinweise der Veröffentlichung und bietet Jetzt aktualisieren, Später oder Überspringen. Das Setup wird nur mit
  passender SHA-256 (GitHub-`digest` oder `.sha256`-Datei) gestartet; abschaltbar unter Optionen → Allgemein.
  Eine Veröffentlichung braucht das Tag `vX.Y.Z` und die Datei `TUNAS-Download-Manager-Setup-X.Y.Z.exe`.
  Die Setups liegen unter den Releases des öffentlichen Repositorys
  [tunas-download-manager-oss](https://github.com/tunahanakguen/tunas-download-manager-oss/releases/latest).
  Neue Fassung: Version in beiden csproj erhöhen, `installer\build.ps1`, dann
  `gh release create vX.Y.Z artifacts\TUNAS-Download-Manager-Setup-X.Y.Z.exe* --repo tunahanakguen/tunas-download-manager-oss`.
  Fassungen bis 0.7.0 fragen noch das frühere Repository `tunas-download-manager-releases`; dort liegt deshalb
  zusätzlich 0.8.0, die danach hier nachsieht.

- **Import/Export** (Symbolleiste → „Liste“): Link-Listen aus Textdateien (Links irgendwo im Text), IDM-Listen
  (`.ef2`, mit Referer und User-Agent) und HTML-Seiten einlesen; die Auswahl zeigt alle Links (bei HTML vorausgewählt nach
  Dateityp), Links aus der Liste werden übersprungen. Eine Datei ins Fenster ziehen importiert ebenfalls.
  Exportieren schreibt die ganze Liste (oder die markierten Einträge) als `.ef2` für IDM bzw. TUNAS auf einem anderen PC
  oder als Textdatei – Cookies nie.

- **FTP** (`ftp://`, anonym oder `ftp://benutzer:passwort@server/pfad`): wie HTTP mit mehreren Verbindungen
  (je Segment eine eigene Verbindung mit `REST`), Fortsetzen nach Pause, Prüfung von Größe und Datum (`SIZE`, `MDTM`)
  beim Fortsetzen und in Sync-Warteschlangen. Server ohne `REST` laden mit einer Verbindung; ein Verbindungslimit des
  Servers (421) bremst nur die überzähligen Verbindungen. Passwörter in der Adresse liegen nur verschlüsselt in Liste
  und Stand-Datei, werden als `***` angezeigt und nicht exportiert. FTP geht nicht über den Proxy.
- **FTPS** (verschlüsselt, wie FileZilla): `ftpes://` mit explizitem TLS (`AUTH TLS`, Port 21) und `ftps://` mit
  implizitem TLS (Port 990). Steuer- und Datenverbindungen sind verschlüsselt (`PROT P`); das Zertifikat wird wie bei
  HTTPS von Windows geprüft – selbst signierte Server werden abgelehnt, bevor Benutzer und Passwort gesendet werden.

- **Site-Grabber** (Symbolleiste → „Grabber“): durchsucht eine Website ab einer Startseite bis zur gewählten Tiefe
  (0–5 Ebenen, höchstens N Seiten) und sammelt Links auf Dateien – alle oder nach Typ (Bilder, Dokumente, Audio, Video,
  Archive, eigene Endungen). Nur Seiten derselben Website bzw. unterhalb des Startordners; robots.txt wird beachtet
  (abschaltbar), auch bei Weiterleitungen. Gelesen werden nur HTML-Seiten (bis 5 MB), die Dateien selbst nicht.
  „Stopp“ behält das bisher Gefundene. Danach wie bei „Alle Links“ auswählen; Referer ist die Seite mit dem Link.

- **Batch-Downloads**: In „URL hinzufügen“ erzeugen Platzhalter viele Adressen – `[1-20]`, `[01-20]` mit
  führenden Nullen, `[0-100:5]` in Schritten, `[a-z]`; mehrere werden kombiniert (`s[1-3]e[01-12]` = 36). Höchstens 10 000.
- **Wenn fertig → Programm starten:** Im Zeitplaner startet eine Warteschlange danach ein Programm, Skript oder eine Datei
  (mit Argumenten), ohne Countdown.

Installer bauen (braucht [Inno Setup 6](https://jrsoftware.org/isinfo.php), z. B. `winget install JRSoftware.InnoSetup`):

```powershell
.\installer\build.ps1
```

Ergebnis: `artifacts\TUNAS-Download-Manager-Setup-<Version>.exe`.

## Sprachen

- Deutsch und Englisch (Optionen → Allgemein → „Sprache · Language“; „Wie Windows“ nimmt Deutsch nur bei deutschem
  Windows). Der deutsche Text ist im Code zugleich der Schlüssel: `L.T("Speichern in")` in C#, `{l:T 'Speichern in'}` in XAML.
  Die Übersetzungen stehen in `src/TunasDM.Core/Localization/en.json`; fehlt eine, erscheint der deutsche Text.
  Ein Test prüft, dass jeder Text im Code übersetzt ist und die Platzhalter `{0}` übereinstimmen.

## Design

- **Wie Windows** (Standard), **dunkel** oder **hell** (Optionen → Allgemein → Design). Schwarz-weiß wie alle TUNAS-Programme,
  dazu **Blau** als Erkennungsfarbe des Download Managers: Logo und Symbol (blauer Grund statt schwarz, damit er sich in
  der Taskleiste vom TUNAS Converter unterscheidet), Akzent für Schaltflächen, Schalter und Fortschritt.
  Gilt nach einem Neustart von TUNAS.

## Sicherheit und Alltag

- **Cookies** aus dem Browser liegen nur verschlüsselt (Windows DPAPI) in der Liste und in den Stand-Dateien.
  Bei Weiterleitungen auf einen anderen Host (oder von https auf http) gehen sie nicht mit.
- **Speicherplatz:** Vor dem Anlegen prüft TUNAS, ob die Datei auf das Laufwerk passt, auch die 4-GB-Grenze von FAT32.
- **Doppelte Links:** Steht eine Adresse schon in der Liste, fragt TUNAS: fortsetzen bzw. Datei zeigen,
  unter neuem Namen laden oder den alten Eintrag ersetzen.
- **Abgelaufene Links** (403/404/410): Rechtsklick → „Adresse aktualisieren“. Solange das Fenster offen ist,
  übernimmt TUNAS den nächsten Download aus dem Browser als neue Adresse und lädt dort weiter, wo er stand.
- **Proxy:** Optionen → Verbindung: wie Windows, keiner oder eigener (HTTP, HTTPS, SOCKS5, mit Anmeldung;
  das Passwort wird verschlüsselt gespeichert). Gilt auch für yt-dlp.

## Bauen und testen

```bash
dotnet build
```

```bash
dotnet test
```

## App starten

```bash
dotnet run --project src/TunasDM.App
```

Zum Ausprobieren ohne Internet gibt es einen lokalen Testserver (Größe in MB, Port, Verzögerung in ms):

```bash
dotnet run --project tools/TunasDM.TestServer -- 300 8765 4
```

Dann in der App `http://127.0.0.1:8765/files/test.bin` hinzufügen.

## Konsole

```bash
dotnet run --project src/TunasDM.Cli -- https://example.com/datei.zip -o C:\Downloads -c 8 -l 2M
```

Strg+C pausiert; derselbe Befehl setzt fort.

## Lizenz

Code: GPL-3.0 (siehe [LICENSE](LICENSE)). Logo und Name „TUNAS“ sind davon ausgenommen und bleiben Markenzeichen
von Tunahan A. – was erlaubt ist, steht in [TRADEMARKS.md](TRADEMARKS.md). Verwendete Bausteine:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Bauen

- Lokal: `installer\build.ps1` (braucht .NET 10 SDK und Inno Setup 6).
- GitHub Actions ([build.yml](.github/workflows/build.yml)): Tests und Setup bei jedem Push; bei einem Tag `v*`
  werden Programmdateien und Setup über SignPath signiert, sobald `SIGNPATH_ORGANIZATION_ID`,
  `SIGNPATH_PROJECT_SLUG` (Variablen) und `SIGNPATH_API_TOKEN` (Secret) gesetzt sind
  ([CODE_SIGNING_POLICY.md](CODE_SIGNING_POLICY.md)). Im öffentlichen Repository entsteht dabei ein
  Release-Entwurf (aus dem privaten Repository nur mit dem Secret `RELEASES_TOKEN`).
