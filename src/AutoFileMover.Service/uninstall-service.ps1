#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Stops and removes the Auto File Mover Windows service.
    Settings and logs in %ProgramData%\AutoFileMover are kept.
#>
$ErrorActionPreference = 'Stop'
$name = 'AutoFileMover'

$svc = Get-Service -Name $name -ErrorAction SilentlyContinue
if (-not $svc) { Write-Host "Service '$name' is not installed."; return }

if ($svc.Status -ne 'Stopped') { Stop-Service -Name $name -Force }
sc.exe delete $name | Out-Null
Write-Host "Service '$name' removed. Settings and logs were left in $env:ProgramData\AutoFileMover."
