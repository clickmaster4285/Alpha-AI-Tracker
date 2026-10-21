#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Install AlphaAITracker.Service as a LocalSystem Windows Service (P1 lab / production).

.DESCRIPTION
  Copies the Release (or -Source) build into Program Files, writes
  %ProgramData%\AlphaAITracker\dlp\service.env, registers SCM auto-start + failure
  recovery, and starts the service.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install-service.ps1
  powershell -ExecutionPolicy Bypass -File .\install-service.ps1 -Source "C:\path\to\publish\Service"
#>
[CmdletBinding()]
param(
    [string]$Source = "",
    [string]$ServerUrl = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$ServiceName = "AlphaAITracker.Service"
$DisplayName = "Alpha AI Tracker DLP"
$InstallDir = Join-Path $env:ProgramFiles "AlphaAITracker\Service"
$DataDir = Join-Path $env:ProgramData "AlphaAITracker\dlp"
$ExeName = "AlphaAITracker.Service.exe"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Stop-IfRunning {
    Get-Process -Name "AlphaAITracker.Service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne "Stopped") {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
        sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }
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

# Resolve server URL from param → client .env → existing env
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
if (-not $ServerUrl) {
    Write-Warning "ALPHA_SERVER_URL not found — service.env will omit it (device.auth from tracker login still works)."
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
)
if ($ServerUrl) {
    $envLines += "ALPHA_SERVER_URL=$ServerUrl"
}
Set-Content -Path $serviceEnvPath -Value ($envLines -join "`r`n") -Encoding UTF8
Write-Host "Wrote $serviceEnvPath"

$binPath = Join-Path $InstallDir $ExeName
# quoted binPath required when path has spaces
$quoted = "`"$binPath`""
sc.exe create $ServiceName binPath= $quoted start= auto DisplayName= $DisplayName obj= LocalSystem | Out-Null
if ($LASTEXITCODE -ne 0) {
    # sc create prints to stdout; retry with CreateService via New-Service
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName $DisplayName -StartupType Automatic -ErrorAction Stop | Out-Null
}
sc.exe description $ServiceName "Alpha AI Tracker Security & DLP enforcement (USB / policy / alerts)." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$svc = Get-Service -Name $ServiceName
Write-Host "Service status: $($svc.Status)"
Write-Host ""
Write-Host "Next: ensure the desktop tracker is logged in (writes $DataDir\device.auth),"
Write-Host "then check Event Viewer → Application / service logs, or:"
Write-Host "  Get-Content `"$DataDir\service.env`""
Write-Host "  sc.exe qc $ServiceName"
Write-Host ""
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$ScriptDir\uninstall-service.ps1`""
