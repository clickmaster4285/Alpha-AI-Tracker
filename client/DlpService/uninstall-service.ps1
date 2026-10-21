#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stop and remove the AlphaAITracker.Service Windows Service.
#>
[CmdletBinding()]
param(
    [switch]$RemoveFiles
)

$ErrorActionPreference = "Stop"
$ServiceName = "AlphaAITracker.Service"
$InstallDir = Join-Path $env:ProgramFiles "AlphaAITracker\Service"

Get-Process -Name "AlphaAITracker.Service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") {
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Deleted service $ServiceName"
} else {
    Write-Host "Service $ServiceName is not registered"
}

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Removed $InstallDir"
}

Write-Host "Done. Policy/alert data under %ProgramData%\AlphaAITracker\dlp was kept (pass nothing else to wipe manually)."
