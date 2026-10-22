; TUNAS Download Manager – Installer (Inno Setup 6)
; Bauen: installer\build.ps1 (veröffentlicht App und Host, dann ISCC).
; Installiert nur für den aktuellen Benutzer nach %LocalAppData%\Programs – ohne Administratorrechte,
; passend zur Browser-Anmeldung unter HKCU.

#define AppName "TUNAS Download Manager"
#define AppExe "TunasDM.exe"
#ifndef AppVersion
  #define AppVersion "0.8.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#define DataDir "{localappdata}\TUNAS Download Manager"

[Setup]
AppId={{6F0B8E52-3C1D-4D7A-9C1E-7A54D1B2C3E9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Tunahan A.
AppCopyright=Copyright © 2026 Tunahan A.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
OutputDir=..\artifacts
OutputBaseFilename=TUNAS-Download-Manager-Setup-{#AppVersion}
SetupIconFile=..\branding\TunasDownloadManager.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-image.bmp,wizard-image-200.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-200.bmp
WizardImageBackColor=$823F0A
Compression=lzma2/ultra64
SolidCompression=yes
ShowLanguageDialog=auto
CloseApplications=no
; Solange das Setup läuft, startet der Browser-Host die App nicht (sie würde Dateien sperren).
SetupMutex=TunasDMSetup
RestartApplications=no

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
de.AutostartTask=Mit Windows starten (im Infobereich)
en.AutostartTask=Start with Windows (in the notification area)
de.VideoToolsTask=Video-Werkzeuge yt-dlp und ffmpeg herunterladen (etwa 220 MB, im Hintergrund)
en.VideoToolsTask=Download the video tools yt-dlp and ffmpeg (about 220 MB, in the background)
de.ExtensionInfo=Browser-Erweiterung einrichten (Anleitung)
en.ExtensionInfo=Set up the browser extension (instructions)
de.RemoveData=Sollen auch die Download-Liste, die Einstellungen und die Video-Werkzeuge gelöscht werden?%n%nDie heruntergeladenen Dateien bleiben in jedem Fall erhalten.
en.RemoveData=Also delete the download list, the settings and the video tools?%n%nDownloaded files are kept in any case.
de.Integration=Integration:
en.Integration=Integration:

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:Integration}"
Name: "videotools"; Description: "{cm:VideoToolsTask}"; GroupDescription: "{cm:Integration}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "extension.html"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\TRADEMARKS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\PRIVACY.md"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Beim Update: alte Erweiterungsdateien entfernen, damit nichts Veraltetes liegen bleibt.
Type: filesandordirs; Name: "{app}\browser"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Autostart (die App liest diesen Eintrag als Einstellung „Mit Windows starten“).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" /tray"; Tasks: autostart; Flags: uninsdeletevalue
; Beim Deinstallieren entfernen, auch wenn die App sie selbst angelegt hat.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppName}"; \
  Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.tunas.dm"; ValueType: none; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.tunas.dm"; ValueType: none; Flags: dontcreatekey uninsdeletekey
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\com.tunas.dm"; ValueType: none; Flags: dontcreatekey uninsdeletekey

[Run]
; Erster Start im Infobereich meldet den Browser-Host bei Chrome und Edge an.
Filename: "{app}\{#AppExe}"; Parameters: "/tray /tools"; Tasks: videotools; Flags: nowait
Filename: "{app}\{#AppExe}"; Parameters: "/tray"; Tasks: not videotools; Flags: nowait
Filename: "{app}\extension.html"; Description: "{cm:ExtensionInfo}"; Flags: postinstall shellexec nowait skipifsilent
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: postinstall nowait skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{#DataDir}\host"
Type: files; Name: "{#DataDir}\com.tunas.dm.json"
Type: files; Name: "{#DataDir}\com.tunas.dm.firefox.json"

[Code]
function IsAppRunning(): Boolean;
var
  Output: TExecOutput;
  ResultCode: Integer;
begin
  Result := False;
  if ExecAndCaptureOutput(ExpandConstant('{sys}\tasklist.exe'), '/FI "IMAGENAME eq {#AppExe}" /FO CSV /NH', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode, Output) then
    Result := Pos(Lowercase('"{#AppExe}"'), Lowercase(StringJoin(#13#10, Output.StdOut))) > 0;
end;

{ Laufende App erst bitten, sich zu beenden (Downloads sauber anhalten), notfalls hart beenden. }
procedure StopApp(const Exe: String);
var
  ResultCode, Waited: Integer;
begin
  if not IsAppRunning() then
    Exit;
  if FileExists(Exe) then
    Exec(Exe, '/exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while IsAppRunning() and (Waited < 15000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
  if IsAppRunning() then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  { Ohne diesen Verweis findet auch ein älterer Browser-Host die App nicht und startet sie nicht mitten im
    Update neu (gesperrte Dateien). Die App schreibt ihn beim ersten Start wieder. }
  DeleteFile(ExpandConstant('{#DataDir}\host\TunasDM.app.txt'));
  StopApp(ExpandConstant('{app}\{#AppExe}'));
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopApp(ExpandConstant('{app}\{#AppExe}'))
  else if CurUninstallStep = usPostUninstall then
  begin
    if not UninstallSilent and DirExists(ExpandConstant('{#DataDir}')) and
       (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{#DataDir}'), True, True, True);
  end;
end;
