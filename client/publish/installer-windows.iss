; Inno Setup script for Alpha AI Tracker
; Produces a single .exe installer with wizard UI
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)

; Generated app identifiers from APP_IDENTIFIERS
#include "windows\windows_vars.iss"

#define MyAppAssocName MyAppName + " File"
#define MyAppAssocExt ".myp"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

; Read version from VERSION file (single source of truth)
#define VERSION_FILE FileOpen("..\\VERSION")
#define MyAppVersion FileRead(VERSION_FILE)
#undef VERSION_FILE

[Setup]
AppId={{B0A0A0A0-1A2B-3C4D-5E6F-7A8B9C0D1E2F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
ChangesAssociations=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\installers
OutputBaseFilename=AlphaAITracker-Setup-{#MyAppVersion}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=avalonia-logo.ico
CloseApplications=force
AppMutex={#APP_MUTEX}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CreateUninstallRegKey=yes
; Force silent kill before any install steps

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Platform\Linux\*;Service\*"
Source: "windows\Service\*"; DestDir: "{app}\Service"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: DirExists(ExpandConstant('{src}\windows\Service'))
Source: "windows\config.enc"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist; Check: FileExists(ExpandConstant('{src}\windows\config.enc'))
Source: "..\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocExt}\OpenWithProgids"; ValueType: string; ValueName: "{#MyAppAssocKey}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}"; ValueType: string; ValueName: ""; ValueData: "{#MyAppAssocName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKA; Subkey: "Software\Classes\{#MyAppAssocKey}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]

// ────────────────────────────────────────────────
// KillRunningInstance — Forcefully terminates any
// running Alpha AI Tracker process before setup.
// Uses taskkill to ensure the process is fully gone.
//
// ⚠️ NEVER pass /T (tree-kill) here. The self-updater launches this installer as a
// DESCENDANT of the running app (app → cmd script → setup), so a tree-kill would
// terminate the updater's OWN cmd script and this installer itself before any file
// is written — the update silently never happens (root cause of the 2026-08-12
// "Windows downloads but doesn't update" bug). The updater now exits the app
// itself before the installer runs, so this is only a safety net for manual
// installs over a running instance. Kill by image name ONLY.
// ────────────────────────────────────────────────
function KillRunningInstance: Boolean;
var
  ResultCode: Integer;
  KillCount: Integer;
begin
  Result := True;
  KillCount := 0;

  // Kill by image name ONLY (no /T — see warning above). The exe name is derived
  // from APP_IDENTIFIERS via windows_vars.iss, never hardcoded here.
  if Exec('taskkill', '/F /IM {#MyAppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then KillCount := KillCount + 1;

  // Also stop the DLP Windows Service before replacing its binary.
  Exec('sc.exe', 'stop AlphaAITracker.Service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Exec('taskkill', '/F /IM AlphaAITracker.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then KillCount := KillCount + 1;

  // Small pause to let OS release file handles
  if KillCount > 0 then
    Exec('ping', '127.0.0.1 -n 2 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallDlpService;
var
  ResultCode: Integer;
  ExePath: string;
  DataDir: string;
  EnvPath: string;
begin
  ExePath := ExpandConstant('{app}\Service\AlphaAITracker.Service.exe');
  if not FileExists(ExePath) then
    Exit;

  DataDir := ExpandConstant('{commonappdata}\AlphaAITracker\dlp');
  ForceDirectories(DataDir);
  EnvPath := DataDir + '\service.env';
  // Lab/ship default: enable DLP when the service is installed. Server URL comes
  // from device.auth (tracker login) or an admin-edited service.env.
  if not FileExists(EnvPath) then
    SaveStringToFile(EnvPath,
      'ALPHA_DLP_ENABLED=true' + #13#10 +
      'ALPHA_DLP_USB_BLOCK=true' + #13#10 +
      'ALPHA_DLP_WEB_BLOCK=true' + #13#10 +
      'ALPHA_DLP_POLICY_POLL_SEC=60' + #13#10 +
      'ALPHA_DLP_ALERT_SYNC_SEC=30' + #13#10, False);

  Exec('sc.exe', 'stop AlphaAITracker.Service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete AlphaAITracker.Service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'create AlphaAITracker.Service binPath= "' + ExePath + '" start= auto DisplayName= "Alpha AI Tracker DLP" obj= LocalSystem',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'description AlphaAITracker.Service "Alpha AI Tracker Security & DLP enforcement (USB / policy / alerts)."',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'failure AlphaAITracker.Service reset= 86400 actions= restart/5000/restart/10000/restart/30000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'start AlphaAITracker.Service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure RemoveDlpService;
var
  ResultCode: Integer;
begin
  Exec('sc.exe', 'stop AlphaAITracker.Service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'delete AlphaAITracker.Service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM AlphaAITracker.Service.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// ────────────────────────────────────────────────
// InitializeSetup — Called when setup starts.
// Returns True to continue, False to abort.
// ────────────────────────────────────────────────
function InitializeSetup: Boolean;
begin
  Result := True;
  KillRunningInstance;
end;

// ────────────────────────────────────────────────
// InitializeUninstall — Called when uninstall starts.
// Also kills running instances before removing files.
// ────────────────────────────────────────────────
function InitializeUninstall: Boolean;
begin
  Result := True;
  RemoveDlpService;
  KillRunningInstance;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallDlpService;
end;

procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;
