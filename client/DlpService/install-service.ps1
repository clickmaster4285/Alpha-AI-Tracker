#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install the Windows DLP service (APP_IDENTIFIERS WINDOWS_SERVICE_NAME) as LocalSystem.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install-service.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = "",
    [string]$ServerUrl = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$IdsFile = Join-Path (Split-Path $ScriptDir -Parent) "APP_IDENTIFIERS"

function Read-AppIdentifiers([string]$Path) {
    $map = @{}
    if (-not (Test-Path $Path)) { throw "APP_IDENTIFIERS not found at $Path" }
    Get-Content $Path | ForEach-Object {
        $line = $_.Trim()
        if ($line.Length -eq 0 -or $line.StartsWith('#')) { return }
        if ($line -match '^\s*([A-Z0-9_]+)\s*=\s*"(.*)"\s*$') {
            $map[$Matches[1]] = $Matches[2]
        } elseif ($line -match "^\s*([A-Z0-9_]+)\s*=\s*'(.*)'\s*$") {
            $map[$Matches[1]] = $Matches[2]
        } elseif ($line -match '^\s*([A-Z0-9_]+)\s*=\s*([^#\s]+)') {
            $map[$Matches[1]] = $Matches[2]
        }
    }
    return $map
}

$ids = Read-AppIdentifiers $IdsFile
$AppMutex = $ids['APP_MUTEX']
if (-not $AppMutex) { $AppMutex = 'AlphaAITracker' }
$DisplayName = $ids['DISPLAY_NAME']
if (-not $DisplayName) { $DisplayName = 'Alpha AI Tracker' }
$ServiceName = $ids['WINDOWS_SERVICE_NAME']
if (-not $ServiceName) { $ServiceName = "$AppMutex.Service" }
$ServiceDisplayName = $ids['WINDOWS_SERVICE_DISPLAY_NAME']
if (-not $ServiceDisplayName) { $ServiceDisplayName = "$DisplayName DLP" }
$PipeName = $ids['WINDOWS_DLP_PIPE_NAME']
if (-not $PipeName) { $PipeName = "$AppMutex.Dlp" }
$MaintTask = $ids['WINDOWS_SERVICE_MAINT_TASK']
if (-not $MaintTask) { $MaintTask = "${AppMutex}SvcMaint" }

$InstallDir = Join-Path $env:ProgramFiles "$AppMutex\Service"
$DataDir = Join-Path $env:ProgramData "$AppMutex\dlp"
$ExeName = "$ServiceName.exe"

function Stop-IfRunning {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        $tr = "cmd.exe /c sc.exe stop $ServiceName & sc.exe delete $ServiceName & taskkill /F /IM $ExeName"
        schtasks.exe /Create /TN $MaintTask /TR $tr /SC ONCE /ST 00:00 /RU SYSTEM /F | Out-Null
        schtasks.exe /Run /TN $MaintTask | Out-Null
        Start-Sleep -Seconds 3
        schtasks.exe /Delete /TN $MaintTask /F | Out-Null
    }
    Get-Process -Name $ServiceName -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

if (-not $Source) {
    $Source = Join-Path $ScriptDir "bin\Release\net10.0"
    if (-not (Test-Path (Join-Path $Source $ExeName))) {
        $Source = Join-Path $ScriptDir "bin\Release\net10.0\win-x64"
    }
}

if (-not $SkipBuild -and -not (Test-Path (Join-Path $Source $ExeName))) {
    Write-Host "Building Release DlpService..."
    Push-Location $ScriptDir
    try {
        bash ../publish/generate-windows-vars.sh 2>$null
        dotnet build -c Release -v q
        $Source = Join-Path $ScriptDir "bin\Release\net10.0"
    } finally {
        Pop-Location
    }
}

$srcExe = Join-Path $Source $ExeName
if (-not (Test-Path $srcExe)) {
    throw "Service binary not found at $srcExe. Build DlpService Release first or pass -Source."
}

if (-not $ServerUrl) {
    $ServerUrl = $env:ALPHA_SERVER_URL
}
if (-not $ServerUrl) {
    $clientEnv = Join-Path (Split-Path $ScriptDir -Parent) ".env"
    if (Test-Path $clientEnv) {
        foreach ($line in Get-Content $clientEnv) {
            if ($line -match '^\s*ALPHA_SERVER_URL\s*=\s*(.+)$') {
                $ServerUrl = $Matches[1].Trim().Trim('"').Trim("'")
                break
            }
        }
    }
}

Write-Host "Stopping any existing $ServiceName / interactive instance..."
Stop-IfRunning

Write-Host "Installing to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $InstallDir -Recurse -Force

$serviceEnvPath = Join-Path $DataDir "service.env"
$envLines = @(
    "ALPHA_DLP_ENABLED=true"
    "ALPHA_DLP_USB_BLOCK=true"
    "ALPHA_DLP_WEB_BLOCK=true"
    "ALPHA_DLP_POLICY_POLL_SEC=60"
    "ALPHA_DLP_ALERT_SYNC_SEC=30"
    "ALPHA_DLP_PIPE_NAME=$PipeName"
)
if ($ServerUrl) {
    $envLines += "ALPHA_SERVER_URL=$ServerUrl"
}
Set-Content -Path $serviceEnvPath -Value ($envLines -join "`r`n") -Encoding UTF8
Write-Host "Wrote $serviceEnvPath"

$binPath = Join-Path $InstallDir $ExeName
$quoted = "`"$binPath`""
sc.exe create $ServiceName binPath= $quoted start= auto DisplayName= "`"$ServiceDisplayName`"" obj= LocalSystem | Out-Null
if ($LASTEXITCODE -ne 0) {
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $ServiceDisplayName -StartupType Automatic -ErrorAction Stop | Out-Null
}
sc.exe description $ServiceName "$DisplayName Security & DLP enforcement (USB / policy / alerts)." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 2

$lockedSddl = 'D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCLCSWLOCRRC;;;BA)'
sc.exe sdset $ServiceName $lockedSddl | Out-Null
Write-Host "Applied locked service DACL (SY full, BA query-only) for $ServiceName"

$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)"
Write-Host "Protected uninstall: client.exe --protected-uninstall --code <code>"
Write-Host "Lab force-remove: uninstall-service.ps1 -Force"
