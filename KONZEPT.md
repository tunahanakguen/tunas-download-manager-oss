# Konzept: TUNAS Download Manager

Ein eigenständiger Download-Manager für Windows mit der gewohnten Bedienung klassischer Download-Manager.
Eigener Code, TUNAS-Design (blauer Akzent), Logo aus `branding/`.

## Ziele

1. **Schnell:** mehrere Verbindungen pro Datei, dynamische Segmentierung, Verbindungen wiederverwenden.
2. **Robust:** jederzeit fortsetzbar, übersteht Abstürze und Stromausfall, erkennt geänderte Dateien.
3. **Integriert:** Browser-Downloads werden übernommen, über Videos erscheint ein Download-Symbol (auch YouTube).
4. **Organisiert:** Kategorien, Queues, Scheduler, Synchronisation.

Nicht geplant: Kernel-Treiber zum Abfangen des Netzverkehrs, Einwahl-/Modem-Funktionen und IE-Module.

## Technik

| Bereich | Wahl | Grund |
|---|---|---|
| Sprache/Runtime | C# / .NET 10 (`net10.0-windows`) | wie TUNAS Converter, SDK 10.0.402 vorhanden |
| Oberfläche | WPF + CommunityToolkit.Mvvm + WPF-UI (Fluent, Mica) | wie Converter; WPF-UI für runde, moderne Windows-11-Optik im TUNAS-Schwarz-Weiß |
| HTTP | `SocketsHttpHandler` / `HttpClient` | HTTP/1.1 + HTTP/2, Keep-Alive, Proxy, gzip/brotli, TLS |
| FTP | FluentFTP (NuGet) | spart einen eigenen FTP-Stack |
| Datenhaltung | SQLite (`Microsoft.Data.Sqlite`) | Download-Liste und Einstellungen; Segmentstände bleiben in der Stand-Datei neben dem Download (`*.tunasdm.json`, mit Flush auf die Platte) |
| Video | **yt-dlp** + **ffmpeg** (mitgeliefert, aktualisierbar) | YouTube und Co. ändern sich ständig, yt-dlp wird gepflegt |
| Browser | eigene Erweiterung (Manifest V3) für Chrome, Edge, Firefox | Native Messaging |
| Tests | xUnit + lokaler Test-HTTP-Server | Range, Abbrüche und langsame Server simulieren |

## Architektur

```
┌──────────── Browser ────────────┐
│ TUNAS-Erweiterung (MV3)         │
│  background · content · page    │
└───────────────┬─────────────────┘
                │ Native Messaging (stdio, JSON)
┌───────────────▼─────────────────┐
│ TunasDM.Host.exe (klein)        │  prüft Server-Prozess, startet App bei Bedarf
└───────────────┬─────────────────┘
                │ Named Pipe  \\.\pipe\TunasDM.<SessionId>
┌───────────────▼─────────────────────────────────────────────┐
│ TunasDM.exe (WPF-App, eine Instanz)                         │
│  ├─ UI (Hauptliste, Kategorien, Queues, Dialoge, Tray)      │
│  ├─ Core                                                    │
│  │   ├─ DownloadEngine  (Segmente, Verbindungen, Limiter)   │
│  │   ├─ Protocols       (HTTP/S, FTP, HLS/DASH)             │
│  │   ├─ MediaService    (yt-dlp, ffmpeg)                    │
│  │   ├─ QueueScheduler  (Queues, Zeitplan, Sync, Aktionen)  │
│  │   └─ Integration     (Pipe-Server, Zwischenablage, CLI)  │
│  └─ Storage (SQLite + Zustandsdateien)                      │
└─────────────────────────────────────────────────────────────┘
```

Projektstruktur:

```
src/
  TunasDM.Core/        Engine, Protokolle, Queues, Storage (ohne UI, testbar)
  TunasDM.App/         WPF-Oberfläche
  TunasDM.Host/        Native-Messaging-Host (Konsolen-Exe, sehr klein)
  TunasDM.Cli/         Test-Konsole für die Engine (Phase 1)
extension/             Browser-Erweiterung (Chromium + Firefox-Manifest)
tests/
  TunasDM.Core.Tests/
branding/
```

## Download-Engine

### Ablauf eines Downloads

1. **Erste Verbindung** fragt `GET` mit `Range: bytes=0-` an.
   - Bei `206` mit `Content-Range` ist die Größe bekannt und Segmentierung möglich.
   - Bei `200` ohne Range-Unterstützung wird mit einer einzigen Verbindung geladen; Fortsetzen geht dann nicht.
   - Aus der Antwort kommen Dateiname (`Content-Disposition`, sonst URL), Typ, `ETag` und `Last-Modified`.
2. **Zieldatei vorab anlegen** in voller Größe (`SetLength`, sparse). Jede Verbindung schreibt direkt an
   ihre Position. Ein Zusammensetzen von Teildateien am Ende entfällt, und es wird kein doppelter
   Speicherplatz gebraucht. Während des Ladens heißt die Datei `name.ext.tunasdm`.
3. **Weitere Verbindungen** (bis Maximum, Standard 8, pro Server einstellbar) teilen jeweils
   das größte offene Segment in der Mitte.
4. **Abschluss:** umbenennen, Zeitstempel setzen, optional Prüfsumme, Virenscan, Abschluss-Dialog.

### Segmentmodell (dynamisch)

- Ein Download hat eine **nach Start sortierte Liste von Segmenten** `{Start, Position, Ende}`.
- Eine Verbindung lädt von `Position` bis **vor den Start des nächsten aktiven Segments**.
  Ein Nachfolger gilt erst als aktiv, wenn er Daten empfangen hat. Ein hängender Nachfolger blockiert also nicht.
- **Neue oder frei gewordene Verbindung:**
  1. Hat der direkte Nachfolger noch nicht begonnen, übernimmt sie dessen Bereich.
  2. Sonst sucht sie den größten offenen Rest und teilt ihn: `neuerStart = Position + (Ende − Position) / 2`.
  3. Unter einer Mindestgröße (Standard 1 MiB) wird nicht geteilt.
- **Wiederverwendung:** Eine fertige Verbindung behält ihre TCP/TLS-Verbindung (Keep-Alive) und
  bekommt sofort das nächste Stück.
- **Langsame Verbindungen:** Eine Verbindung, die deutlich unter dem Durchschnitt liegt, wird
  abgebrochen. Ihr Rest fließt zurück in die Verteilung.
- **Anschlussprüfung:** Beim Fortsetzen werden die letzten 4 KiB erneut angefragt und verglichen.
  Weichen sie ab, wird der Bereich verworfen.

### Fortsetzen und Robustheit

- Segmentstände werden alle 2 Sekunden und bei Pause gespeichert (SQLite, Transaktion).
  Gespeichert wird nur, was schon per `Flush` auf der Platte liegt.
- Beim Fortsetzen geht `If-Range: <ETag oder Last-Modified>` mit. Antwortet der Server mit `200` statt `206`,
  hat sich die Datei geändert, und der Nutzer wird gefragt (neu laden oder abbrechen).
  Es gibt die Option „Änderungszeit ignorieren“.
- Abgelaufene Links (403/410) führen zum Dialog „Adresse aktualisieren“. Ein Klick auf denselben Link
  im Browser liefert die neue URL samt Cookies.
- Fehlerbehandlung: Wiederholen mit wachsendem Abstand, Timeout einstellbar.
  Nötig sind Prüfungen für Speicherplatz, FAT32-4-GB-Grenze und Schreibrechte.

### Netzwerk-Optionen

- Proxy: System, HTTP, HTTPS, SOCKS5; Anmeldung pro Proxy.
- Site-Logins: Benutzer und Passwort pro Site-Muster, gespeichert mit Windows DPAPI.
- Cookies, Referer und User-Agent kommen vom Browser mit. Eigene Header pro Download sind möglich.
- **Geschwindigkeitsbegrenzer:** global und pro Download (Token-Bucket). Kontingente wie „40 MB/Stunde“ sind optional.

## Video und Streaming

- **YouTube und andere große Portale:** yt-dlp holt die Formatliste (`-J`), der Nutzer wählt die Qualität,
  yt-dlp lädt, ffmpeg mischt Audio und Video. yt-dlp lässt sich aus der App aktualisieren.
- **Andere Seiten:** Medien-URLs aus der Erweiterung, also direkte Dateien (MP4/WebM/MP3), HLS (`.m3u8`)
  und DASH (`.mpd`). HLS und DASH lädt die eigene Engine (Segmente parallel), ffmpeg setzt sie zusammen.
- Die Erkennung stuft nach Content-Type, Endung und Größe ein und ignoriert Werbung und winzige Clips.

## Browser-Integration

### Erweiterung (MV3, dreischichtig)

| Teil | Aufgabe |
|---|---|
| `background.js` | `webRequest` für Medien und Downloads, `downloads.onCreated` (übernehmen und abbrechen), Kontextmenü („Mit TUNAS laden“, „Alle Links laden“, „Video laden“), Native Port |
| `content.js` | `MutationObserver` auf `video`/`audio`, größtes sichtbares Video bestimmen, Position verfolgen (Scrollen, Größe, Vollbild), Symbol einblenden |
| `page.js` | nur wenn nötig: `fetch`/XHR-Mitschnitt für Seiten, deren Medien-URLs anders nicht zu bekommen sind |

- **Download-Symbol über dem Video:** schwarzes TUNAS-Symbol oben rechts am Video, in einem Shadow-DOM
  (stört das Seiten-CSS nicht). Ein Klick zeigt die Qualitäten (von der App geliefert), die Auswahl startet den Download.
- Übernahme nach Dateitypen-Liste, Ausnahmelisten für Sites und Adressen, eine Taste zum Erzwingen oder Verhindern.
- Firefox bekommt ein eigenes Manifest (`browser_specific_settings`, eigene Host-Registrierung).

### Native Host und Protokoll

- `TunasDM.Host.exe` wird vom Browser gestartet und reicht Nachrichten 1:1 an die Named Pipe weiter.
  Er prüft per `GetNamedPipeServerProcessId`, dass am anderen Ende wirklich `TunasDM.exe` läuft,
  und startet die App, wenn sie nicht läuft.
- Registrierung: `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.tunas.dm` (dazu Edge, Firefox).
  Der Installer erledigt das, die App kann es reparieren.
- **Lesbares JSON-Protokoll** (Version im Handshake):

```json
{ "v": 1, "type": "download", "url": "...", "referrer": "...", "cookies": "...",
  "headers": { "User-Agent": "..." }, "filename": "...", "tabId": 12 }
{ "v": 1, "type": "media",    "url": "...", "kind": "hls", "pageUrl": "...", "tabId": 12 }
{ "v": 1, "type": "showButton", "tabId": 12, "frameId": 0, "formats": [ ... ] }
```

## Verwaltung

- **Hauptliste** mit Spalten Name, Größe, Status, Restzeit, Geschwindigkeit, Datum, Beschreibung und Queue.
  Fortschrittsbalken mit Segmenten (geladen, offen, Startpunkte).
- **Kategorien:** Allgemein, Musik, Video, Dokumente, Programme, Archive. Jede hat Endungen und einen eigenen
  Zielordner, eigene Kategorien sind möglich.
- **Queues:** Haupt-Queue, beliebig viele weitere, „x Dateien gleichzeitig“ pro Queue.
  **Sync-Queues** prüfen regelmäßig per `HEAD`/`If-Modified-Since` und laden Änderungen nach.
- **Scheduler:** einmalig, täglich oder an Wochentagen; Start- und Stoppzeit; Wiederholungen bei Fehlern.
  Danach: Herunterfahren, Ruhezustand, Programm starten oder TUNAS beenden.
- **Dialoge:** Download-Info (lädt schon im Hintergrund), Fortschritt pro Datei mit Verbindungstabelle,
  Abschluss (Öffnen / Ordner öffnen), Duplikat (umbenennen, überschreiben, fortsetzen).
- **Weitere Eingabewege:** Zwischenablage-Überwachung, Drag & Drop, „URL hinzufügen“,
  Batch-Downloads (`file[01-99].zip`), Import aus Text- und HTML-Dateien, Export und Import der Liste.
- **Kommandozeile:** `TunasDM.exe /d URL [/p Pfad] [/f Name] [/a] [/n] [/q] [/s]` (kompatibel zu IDM).
- **Site Grabber** (später): Start-URL, Link-Tiefe, Datei-/Seitenfilter, Projekte speichern, nutzt die Queues.
- **Tray-Symbol**, Autostart, Sprachen Deutsch/Englisch (resx), Hell/Dunkel im Schwarz-Weiß-Design.

## Reihenfolge

| Phase | Inhalt | Fertig, wenn … |
|---|---|---|
| **1 Engine** | Core + Cli: HTTP(S), Range-Prüfung, dynamische Segmente, Fortsetzen, Limiter, Tests mit Test-Server | eine 1-GB-Datei mit 8 Verbindungen lädt, nach Abbruch fortsetzt und per SHA-256 stimmt |
| **2 App** | WPF-Hauptfenster, Liste, Kategorien, Dialoge, Einstellungen, SQLite, Tray, CLI-Parameter | Alltagsdownloads ohne Browser über die Oberfläche |
| **3 Browser** | Host, Pipe, Erweiterung (Chrome/Edge), Download-Übernahme, Kontextmenü | Klick auf einen Download-Link im Browser landet in TUNAS |
| **4 Video** | Video-Symbol, yt-dlp/ffmpeg, HLS/DASH-Engine, Firefox | YouTube-Video in wählbarer Qualität per Symbol |
| **5 Planen** | Queues, Scheduler, Sync-Queues, Aktionen danach | Queue startet nachts und fährt den PC herunter |
| **6 Rest** | FTP, Grabber, Import/Export, Installer (Logo, Host-Registrierung), Updates | Installer auf frischem Windows getestet |

## Rechtliches

- Eigener Code (GPL-3.0), keine Teile, Texte oder Grafiken anderer Download-Manager. Kompatibel sind nur
  offene Schnittstellen: die Listenform `.ef2` und die üblichen Kommandozeilen-Schalter.
- Fremde Bausteine und ihre Lizenzen: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
- Logo und Name „TUNAS“: [TRADEMARKS.md](TRADEMARKS.md).
