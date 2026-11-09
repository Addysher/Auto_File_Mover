<#
.SYNOPSIS
    Builds self-contained single-file exes (no .NET install needed on the target PC).
    Output: .\publish\AutoFileMover.exe (tray app) and .\publish\Service\ (Windows service + install scripts).
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none')

dotnet test .\AutoFileMover.sln -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed — not publishing.' }

dotnet publish .\src\AutoFileMover.Tray\AutoFileMover.Tray.csproj @common -o .\publish
if ($LASTEXITCODE -ne 0) { throw 'Tray publish failed.' }

dotnet publish .\src\AutoFileMover.Service\AutoFileMover.Service.csproj @common -o .\publish\Service
if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }

Get-ChildItem .\publish -Recurse -File | Select-Object FullName, @{n='MB';e={[math]::Round($_.Length / 1MB, 1)}}
