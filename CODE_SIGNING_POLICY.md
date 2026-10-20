# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

*(Kostenlose Code-Signatur durch SignPath.io, Zertifikat von der SignPath Foundation.)*

## What is signed

Only files built from this repository by the [GitHub Actions workflow](.github/workflows/build.yml):

- `TunasDM.exe`, `TunasDM.dll`, `TunasDM.Core.dll`, `TunasDM.Host.exe`
- the installer `TUNAS-Download-Manager-Setup-<version>.exe`

Third-party files inside the installer (.NET runtime, WPF-UI, SQLite, …) are shipped as published by their
maintainers. yt-dlp and FFmpeg are not part of the installer; the program downloads them from their official
projects on request and verifies their checksums.

## Team

| Role | Members |
|---|---|
| Committers and reviewers | [Tunahan A.](https://github.com/tunahanakguen) |
| Approvers | [Tunahan A.](https://github.com/tunahanakguen) |

Every release is approved manually in SignPath before it is signed. All team members use multi-factor
authentication for GitHub and SignPath. Changes from outside contributors are reviewed before they are merged.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the
user or the person installing or operating it. The one exception is the update check against GitHub, which is
on by default and can be switched off in the options. Details: [PRIVACY.md](PRIVACY.md).
