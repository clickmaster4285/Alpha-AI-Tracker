; Inno Setup script for Alpha AI Tracker
; Produces a single .exe installer with wizard UI
; Requires Inno Setup 6+ (https://jrsoftware.org/isinfo.php)

#define MyAppName "Alpha AI Tracker"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Alpha AI"
#define MyAppURL "https://alpha-ai-tracker.example.com"
#define MyAppExeName "client.exe"
#define MyAppAssocName MyAppName + " File"
#define MyAppAssocExt ".myp"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt

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
AppMutex=AlphaAITracker
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CreateUninstallRegKey=yes
; Force silent kill before any install steps

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: checkedonce

[Files]
Source: "windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Platform\Linux\*"
Source: "..\.env.example"; DestDir: "{app}"; DestName: ".env"; Flags: ignoreversion onlyifdoesntexist
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
// ────────────────────────────────────────────────
function KillRunningInstance: Boolean;
var
  ResultCode: Integer;
  KillCount: Integer;
begin
  Result := True;
  KillCount := 0;

  // Try multiple possible executable names
  if Exec('taskkill', '/F /IM client.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then KillCount := KillCount + 1;

  if Exec('taskkill', '/F /IM AlphaAITracker.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    if ResultCode = 0 then KillCount := KillCount + 1;

  // Small pause to let OS release file handles
  if KillCount > 0 then
    Exec('ping', '127.0.0.1 -n 2 -w 1000', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
  KillRunningInstance;
end;

procedure InitializeWizard;
begin
  WizardForm.LicenseAcceptedRadio.Checked := True;
end;
