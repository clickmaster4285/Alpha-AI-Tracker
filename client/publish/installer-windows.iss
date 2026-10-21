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
// Service DACL is locked to LocalSystem (admins cannot sc stop/delete).
// Installer must stop/delete via a one-shot SYSTEM scheduled task BEFORE file copy,
// otherwise DeleteFile on Service\*.dll fails with Access Denied (error 5).
procedure StopDeleteDlpServiceAsSystem;
var
  ResultCode: Integer;
begin
  // Clear failure actions so SCM does not immediately restart after stop/kill.
  Exec('sc.exe', 'failure {#WINDOWS_SERVICE_NAME} reset= 0 actions= //', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe',
    '/Create /TN "{#WINDOWS_SERVICE_MAINT_TASK}" /TR "cmd.exe /c sc.exe stop {#WINDOWS_SERVICE_NAME} & sc.exe delete {#WINDOWS_SERVICE_NAME} & taskkill /F /IM {#WINDOWS_SERVICE_EXE_NAME}" /SC ONCE /ST 00:00 /RU SYSTEM /F',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/Run /TN "{#WINDOWS_SERVICE_MAINT_TASK}"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Wait for SCM + process handles to release (Service\*.dll).
  Exec('ping', '127.0.0.1 -n 6 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('schtasks.exe', '/Delete /TN "{#WINDOWS_SERVICE_MAINT_TASK}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // Belt-and-suspenders: admin may still be able to terminate the process image.
  Exec('taskkill', '/F /IM {#WINDOWS_SERVICE_EXE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('ping', '127.0.0.1 -n 2 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

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

  // Locked service ACL: plain "sc stop" fails for Administrators — use SYSTEM task.
  StopDeleteDlpServiceAsSystem;
  if Exec('taskkill', '/F /IM {#WINDOWS_SERVICE_EXE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then KillCount := KillCount + 1;

  // Small pause to let OS release file handles
  Exec('ping', '127.0.0.1 -n 3 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure LockDlpServiceAcl;
var
  ResultCode: Integer;
begin
  // SY=full; BA=query only — DLP service cannot be stopped from services.msc.
  Exec('sc.exe',
    'sdset {#WINDOWS_SERVICE_NAME} D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure InstallDlpService;
var
  ResultCode: Integer;
  ExePath: string;
  DataDir: string;
  EnvPath: string;
begin
  ExePath := ExpandConstant('{app}\Service\{#WINDOWS_SERVICE_EXE_NAME}');
  if not FileExists(ExePath) then
    Exit;

  DataDir := ExpandConstant('{commonappdata}\{#APP_MUTEX}\dlp');
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
      'ALPHA_DLP_ALERT_SYNC_SEC=30' + #13#10 +
      'ALPHA_DLP_PIPE_NAME={#WINDOWS_DLP_PIPE_NAME}' + #13#10, False);

  // Service was already removed in PrepareToInstall / InitializeSetup; recreate.
  Exec('sc.exe',
    'create {#WINDOWS_SERVICE_NAME} binPath= "' + ExePath + '" start= auto DisplayName= "{#WINDOWS_SERVICE_DISPLAY_NAME}" obj= LocalSystem',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'description {#WINDOWS_SERVICE_NAME} "{#MyAppName} Security & DLP enforcement (USB / policy / alerts)."',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe',
    'failure {#WINDOWS_SERVICE_NAME} reset= 86400 actions= restart/5000/restart/10000/restart/30000',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('sc.exe', 'start {#WINDOWS_SERVICE_NAME}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  LockDlpServiceAcl;
end;

procedure RemoveDlpService;
begin
  // Only reached after teardown.ok (removal code). Prefer SYSTEM maintenance task.
  StopDeleteDlpServiceAsSystem;
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

// Runs immediately before files are installed — ensure Service\*.dll is unlocked.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  KillRunningInstance;
  Result := '';
end;

// ────────────────────────────────────────────────
// InitializeUninstall — Alpha AI Tracker ONLY.
// Hard gate: Apps & features uninstall is refused unless
// teardown.ok exists (written after a verified removal code
// via client.exe --protected-uninstall --code …).
// No break-glass Yes — other installed software is unaffected.
// ────────────────────────────────────────────────
function InitializeUninstall: Boolean;
var
  Gate: string;
begin
  Gate := ExpandConstant('{commonappdata}\{#APP_MUTEX}\dlp\teardown.ok');
  if not FileExists(Gate) then
  begin
    MsgBox(
      '{#MyAppName} cannot be uninstalled without an authorized removal code.'#13#10#13#10 +
      '1. On the web: Employees → Issue Removal Code'#13#10 +
      '2. On this PC run:'#13#10 +
      '   {#MyAppExeName} --protected-uninstall --code <code>'#13#10 +
      '3. Then uninstall again from Apps & features.'#13#10#13#10 +
      'This restriction applies only to {#MyAppName}.',
      mbError, MB_OK);
    Result := False;
    Exit;
  end;

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
