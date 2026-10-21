#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Stop and remove the AlphaAITracker.Service Windows Service.

.DESCRIPTION
  Refuses unless %ProgramData%\AlphaAITracker\dlp\teardown.ok exists
  (created by client --protected-uninstall --code …). Pass -Force only for
  lab/dev recovery — production uninstall must use the removal code path.
#>
[CmdletBinding()]
param(
    [switch]$RemoveFiles,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ServiceName = "AlphaAITracker.Service"
$InstallDir = Join-Path $env:ProgramFiles "AlphaAITracker\Service"
$Gate = Join-Path $env:ProgramData "AlphaAITracker\dlp\teardown.ok"

if (-not $Force -and -not (Test-Path $Gate)) {
    Write-Error @"
Refusing to remove $ServiceName — no authorized teardown.ok.

Run on this PC first:
  client.exe --protected-uninstall --code <code>
(issue the code from web Employees → Issue Removal Code)

Lab only: re-run with -Force
"@
    exit 1
}

function Stop-DeleteAsSystem {
    $tr = 'cmd.exe /c sc.exe stop AlphaAITracker.Service & sc.exe delete AlphaAITracker.Service & taskkill /F /IM AlphaAITracker.Service.exe'
    schtasks.exe /Create /TN "AlphaAITrackerSvcMaint" /TR $tr /SC ONCE /ST 00:00 /RU SYSTEM /F | Out-Null
    schtasks.exe /Run /TN "AlphaAITrackerSvcMaint" | Out-Null
    Start-Sleep -Seconds 3
    schtasks.exe /Delete /TN "AlphaAITrackerSvcMaint" /F | Out-Null
}

Stop-DeleteAsSystem
Write-Host "Deleted service $ServiceName (via SYSTEM task)"

if ($RemoveFiles -and (Test-Path $InstallDir)) {
    Remove-Item -Recurse -Force $InstallDir
    Write-Host "Removed $InstallDir"
}

Write-Host "Done. Policy/alert data under %ProgramData%\AlphaAITracker\dlp was kept."
