; agwinterm installer (Inno Setup 6) — per-user, no admin.
; Build via installer\build.ps1 (publishes to stage\ then runs ISCC on this file).

#define AppName    "agwinterm"
#define AppVersion "0.30.5"
#define AppExe     "Agwinterm.Win32.exe"
#define AppPublisher "Boris Kudriashov"
; The main app defaults new sessions to the Ghostty-capable .NET pty-host as a first-run seed only (it never
; overrides an existing config).
#define DefaultHostArg "--default-session-host server"

[Setup]
AppId={{A7E3F1C2-5B9D-4E6A-8C21-3F0D9B4A7E15}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\agwinterm
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\Agwinterm.Win32\assets\agwinterm.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir=Output
OutputBaseFilename=agwinterm-setup-{#AppVersion}
CloseApplications=no

; Minimal, non-invasive setup (agterm-style): only copy files + create shortcuts. Integrations
; (agwintermctl on PATH, agent hooks, agent skill, shell integration) are OPT-IN from inside the
; app — action palette (Ctrl+Shift+P) -> the "Install ..." entries — so setup never edits PATH or
; writes to the user's profile/config behind their back.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "stage\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "prepare-owner-upgrade.ps1"; Flags: dontcopy

[InstallDelete]
; Remove obsolete files left by older main-product installers.
Type: files; Name: "{app}\agwinterm-ptyhost.exe"
Type: files; Name: "{app}\agwinterm_core.dll"

[Icons]
; Main terminal — its shortcut seeds server on a first run (harmless once configured).
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "{#DefaultHostArg}"; IconFilename: "{app}\assets\agwinterm.ico"
Name: "{autodesktop}\{#AppName}";  Filename: "{app}\{#AppExe}"; Parameters: "{#DefaultHostArg}"; IconFilename: "{app}\assets\agwinterm.ico"; Tasks: desktopicon

[Run]
; Launch-on-finish checkbox (interactive installs only) — passes the seed so the very first run
; (typically this launch) defaults the main app to the Ghostty owner host.
Filename: "{app}\{#AppExe}"; Parameters: "{#DefaultHostArg}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
  Report: AnsiString;
  ReportPath: String;
begin
  Result := '';
  if not FileExists(ExpandConstant('{app}\{#AppExe}')) then Exit;
  ExtractTemporaryFile('prepare-owner-upgrade.ps1');
  ReportPath := ExpandConstant('{tmp}\owner-upgrade-result.txt');
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\prepare-owner-upgrade.ps1') +
    '" -InstallDir "' + ExpandConstant('{app}') + '" -SetupPath "' + ExpandConstant('{srcexe}') + '" -ReportPath "' + ReportPath + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
  begin
    Result := 'Could not save and close the previous version. No application files were replaced.';
    if LoadStringFromFile(ReportPath, Report) then Result := Result + #13#10 + String(Report);
  end;
end;
