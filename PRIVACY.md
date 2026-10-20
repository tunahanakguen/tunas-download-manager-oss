# Datenschutz

TUNAS Download Manager sammelt **keine** Nutzungsdaten, hat keine Telemetrie und kein Konto.
Alles bleibt auf deinem PC (`%LocalAppData%\TUNAS Download Manager`).

Das Programm verbindet sich nur mit:

- **den Servern, von denen du etwas lädst** – mit den Cookies und Kopfzeilen, die der Browser für diesen Download
  mitgibt.
- **GitHub** (`api.github.com`, `github.com`), um nach neuen Versionen zu suchen (abschaltbar in den Optionen)
  und das Update zu laden. GitHub sieht dabei wie bei jedem Seitenaufruf deine IP-Adresse
  ([Datenschutz von GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement)).
- **GitHub** für yt-dlp und FFmpeg, wenn du die Video-Werkzeuge einrichtest.

Die Browser-Erweiterung spricht nur mit dem Programm auf deinem PC (Native Messaging); sie sendet nichts ins
Internet. Passwörter in Download-Adressen werden mit Windows (DPAPI) verschlüsselt gespeichert und nicht
exportiert.
