<#
.SYNOPSIS
  Builds, tests and publishes RingMouse.

.DESCRIPTION
  Build    - Release build of the entire solution
  Test     - all unit tests (protocol, core, DeviceService simulator)
  Publish  - publish\RingMouse\RingMouse.exe            (self-contained single file, win-x64)
             publish\RingMouse-uiAccess\ (folder)       (uiAccess variant for tools\install-uiaccess.ps1)
             publish\probe\ringmouse-probe.exe          (discovery tool, single file)
  All      - everything in sequence (default)

.PARAMETER Version
  Optional version for all assemblies (e.g. 1.2.0 from a release tag). Default: Directory.Build.props.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Target Publish
  .\build.ps1 -Target All -Version 1.2.0
#>
param(
    [ValidateSet('Build', 'Test', 'Publish', 'All')]
    [string]$Target = 'All',

    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$versionArgs = @(if ($Version) { "-p:Version=$Version" })

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed (exit code $LASTEXITCODE)" }
}

Push-Location $PSScriptRoot
try {
    if ($Target -in 'Build', 'All') {
        Write-Host '== Build' -ForegroundColor Cyan
        Invoke-Dotnet build RingMouse.slnx -c Release -nologo @versionArgs
    }

    if ($Target -in 'Test', 'All') {
        Write-Host '== Tests' -ForegroundColor Cyan
        Invoke-Dotnet test RingMouse.slnx -c Release -nologo @versionArgs
    }

    if ($Target -in 'Publish', 'All') {
        Write-Host '== Publish' -ForegroundColor Cyan
        $single = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true', '-p:DebugType=embedded', '-nologo')

        Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue

        Invoke-Dotnet publish src\RingMouse.App\RingMouse.App.csproj @single @versionArgs -o publish\RingMouse
        Invoke-Dotnet publish tools\RingMouse.Probe\RingMouse.Probe.csproj @single @versionArgs -o publish\probe

        # uiAccess-Variante als Ordner (kein Entpacken nativer DLLs nach %TEMP%, alles liegt geschützt unter Program Files)
        # Argumente als Array: PowerShell würde "-p:Name=Wert" sonst in zwei Argumente zerlegen
        $uiAccessArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UiAccess=true',
            '-p:DebugType=embedded', '-p:IntermediateOutputPath=obj\uiaccess\', '-nologo', '-o', 'publish\RingMouse-uiAccess')
        Invoke-Dotnet publish src\RingMouse.App\RingMouse.App.csproj @uiAccessArgs @versionArgs

        # Kontrolle: richtiges Manifest in beiden Varianten?
        $normal = [IO.File]::ReadAllBytes("$PWD\publish\RingMouse\RingMouse.exe")
        $uia = [IO.File]::ReadAllBytes("$PWD\publish\RingMouse-uiAccess\RingMouse.exe")
        $has = { param($bytes, $text) ([Text.Encoding]::UTF8.GetString($bytes)).Contains($text) }
        if (-not (& $has $normal 'uiAccess="false"')) { throw 'Normal variant does not contain uiAccess="false"' }
        if (-not (& $has $uia 'uiAccess="true"')) { throw 'uiAccess variant does not contain uiAccess="true"' }

        Get-ChildItem publish\RingMouse\RingMouse.exe, publish\probe\ringmouse-probe.exe |
            Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
        Write-Host 'Done. RingMouse.exe can be placed anywhere (e.g. %LOCALAPPDATA%\Programs\RingMouse).' -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
