#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs Auto File Mover as a Windows service.
.DESCRIPTION
    Run from an elevated PowerShell in the folder containing AutoFileMover.Service.exe:
        powershell -ExecutionPolicy Bypass -File .\install-service.ps1
    The service runs as LocalSystem by default. It cannot see mapped drive letters or a
    user's OneDrive; use local paths or UNC paths (\\server\share\folder) in settings.json,
    or change the log-on account in services.msc (Log On tab).
#>
param(
    [string]$ExePath = (Join-Path $PSScriptRoot 'AutoFileMover.Service.exe')
)

$ErrorActionPreference = 'Stop'
$name = 'AutoFileMover'

if (-not (Test-Path $ExePath)) { throw "Service executable not found: $ExePath" }
if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
    throw "Service '$name' is already installed. Run uninstall-service.ps1 first."
}

# Shared data folder: let normal users (the tray app) edit settings and read logs.
$data = Join-Path $env:ProgramData 'AutoFileMover'
New-Item -ItemType Directory -Force -Path $data | Out-Null
icacls $data /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null   # BUILTIN\Users : Modify

New-Service -Name $name `
    -BinaryPathName ('"{0}"' -f (Resolve-Path $ExePath)) `
    -DisplayName 'Auto File Mover' `
    -Description 'Moves saw output files into per-job folders.' `
    -StartupType Automatic | Out-Null

sc.exe config $name start= delayed-auto | Out-Null                                    # wait for network at boot
sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

Start-Service -Name $name
Get-Service -Name $name | Format-Table -AutoSize Name, Status, StartType
Write-Host "Settings: $data\settings.json   Logs: $data\Logs"
