# Baut den Installer: App (eigenständig, mit .NET-Laufzeit) und Browser-Host (Einzeldatei) veröffentlichen,
# dann Inno Setup. Ergebnis: artifacts\TUNAS-Download-Manager-Setup-<Version>.exe (+ .sha256)
#
# -Stage All       alles nacheinander (lokal)
# -Stage Publish   nur artifacts\publish füllen (in GitHub Actions danach die Exe-Dateien signieren)
# -Stage Package   Inno Setup aus dem vorhandenen artifacts\publish (danach das Setup signieren)
# -Stage Checksum  .sha256 zum fertigen (signierten) Setup schreiben
param(
    [string]$Configuration = "Release",
    [ValidateSet("All", "Publish", "Package", "Checksum")][string]$Stage = "All"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root "artifacts\publish"

function Publish {
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

    # App: eigenständig, damit ein frisches Windows ohne .NET 10 reicht.
    dotnet publish "$root\src\TunasDM.App\TunasDM.App.csproj" -c $Configuration -r win-x64 --self-contained `
        -p:DebugType=none -p:SatelliteResourceLanguages=de%3Ben -o $publish
    if ($LASTEXITCODE) { throw "Veröffentlichen der App fehlgeschlagen." }

    # Host: eine einzige Exe ohne Laufzeit-Abhängigkeit; die App kopiert sie nach %LocalAppData%\…\host.
    dotnet publish "$root\src\TunasDM.Host\TunasDM.Host.csproj" -c $Configuration -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o "$publish\host-build"
    if ($LASTEXITCODE) { throw "Veröffentlichen des Hosts fehlgeschlagen." }
    Get-ChildItem "$publish\TunasDM.Host.*" | Remove-Item -Force
    Move-Item "$publish\host-build\TunasDM.Host.exe" $publish
    Remove-Item "$publish\host-build" -Recurse -Force
}

function Version {
    (Get-Item "$publish\TunasDM.exe").VersionInfo.ProductVersion -replace '\+.*$', ''
}

function Package {
    $iscc = @(
        "$env:LocalAppData\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw "Inno Setup 6 fehlt (winget install JRSoftware.InnoSetup)." }
    if (-not (Test-Path "$publish\TunasDM.exe")) { throw "artifacts\publish fehlt – erst -Stage Publish." }

    $version = Version
    & $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "$PSScriptRoot\TunasDM.iss"
    if ($LASTEXITCODE) { throw "Inno Setup fehlgeschlagen." }
}

function Checksum {
    $setup = Join-Path $root "artifacts\TUNAS-Download-Manager-Setup-$(Version).exe"
    # Prüfsumme für die Update-Suche (GitHub liefert zusätzlich einen eigenen digest).
    "$((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())  $(Split-Path $setup -Leaf)" |
        Set-Content -Encoding ascii -NoNewline "$setup.sha256"
    Write-Host "Fertig: $setup (+ .sha256)"
}

switch ($Stage) {
    "Publish" { Publish }
    "Package" { Package }
    "Checksum" { Checksum }
    default { Publish; Package; Checksum }
}
