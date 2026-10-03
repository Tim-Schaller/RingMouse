<#
.SYNOPSIS
  Baut, testet und veröffentlicht RingMouse.

.DESCRIPTION
  Build    – Release-Build der gesamten Solution
  Test     – alle Unit-Tests (Protokoll, Core, DeviceService-Simulator)
  Publish  – publish\RingMouse\RingMouse.exe            (self-contained Single-File, win-x64)
             publish\RingMouse-uiAccess\ (Ordner)      (uiAccess-Variante für tools\install-uiaccess.ps1)
             publish\probe\ringmouse-probe.exe          (Discovery-Werkzeug, Single-File)
  All      – alles nacheinander (Standard)

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Target Publish
#>
param(
    [ValidateSet('Build', 'Test', 'Publish', 'All')]
    [string]$Target = 'All'
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') fehlgeschlagen (Exit $LASTEXITCODE)" }
}

Push-Location $PSScriptRoot
try {
    if ($Target -in 'Build', 'All') {
        Write-Host '== Build' -ForegroundColor Cyan
        Invoke-Dotnet build RingMouse.slnx -c Release -nologo
    }

    if ($Target -in 'Test', 'All') {
        Write-Host '== Tests' -ForegroundColor Cyan
        Invoke-Dotnet test RingMouse.slnx -c Release -nologo
    }

    if ($Target -in 'Publish', 'All') {
        Write-Host '== Publish' -ForegroundColor Cyan
        $single = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
            '-p:EnableCompressionInSingleFile=true', '-p:DebugType=embedded', '-nologo')

        Remove-Item -Recurse -Force publish -ErrorAction SilentlyContinue

        Invoke-Dotnet publish src\RingMouse.App\RingMouse.App.csproj @single -o publish\RingMouse
        Invoke-Dotnet publish tools\RingMouse.Probe\RingMouse.Probe.csproj @single -o publish\probe

        # uiAccess-Variante als Ordner (kein Entpacken nativer DLLs nach %TEMP%, alles liegt geschützt unter Program Files)
        # Argumente als Array: PowerShell würde "-p:Name=Wert" sonst in zwei Argumente zerlegen
        $uiAccessArgs = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:UiAccess=true',
            '-p:DebugType=embedded', '-p:IntermediateOutputPath=obj\uiaccess\', '-nologo', '-o', 'publish\RingMouse-uiAccess')
        Invoke-Dotnet publish src\RingMouse.App\RingMouse.App.csproj @uiAccessArgs

        # Kontrolle: richtiges Manifest in beiden Varianten?
        $normal = [IO.File]::ReadAllBytes("$PWD\publish\RingMouse\RingMouse.exe")
        $uia = [IO.File]::ReadAllBytes("$PWD\publish\RingMouse-uiAccess\RingMouse.exe")
        $has = { param($bytes, $text) ([Text.Encoding]::UTF8.GetString($bytes)).Contains($text) }
        if (-not (& $has $normal 'uiAccess="false"')) { throw 'Normale Variante enthält nicht uiAccess="false"' }
        if (-not (& $has $uia 'uiAccess="true"')) { throw 'uiAccess-Variante enthält nicht uiAccess="true"' }

        Get-ChildItem publish\RingMouse\RingMouse.exe, publish\probe\ringmouse-probe.exe |
            Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
        Write-Host 'Fertig. RingMouse.exe kann beliebig abgelegt werden (z.B. %LOCALAPPDATA%\Programs\RingMouse).' -ForegroundColor Green
    }
}
finally {
    Pop-Location
}
