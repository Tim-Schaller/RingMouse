<#
.SYNOPSIS
  Packs the published files into release assets (run after .\build.ps1 -Target Publish).

.DESCRIPTION
  Creates in -OutputDirectory:
    RingMouse.exe              the app (single file, self-contained)
    ringmouse-probe.exe        diagnostic tool
    RingMouse-uiAccess.zip     uiAccess variant + tools\install-uiaccess.ps1 (extract, run the script as administrator)
    LICENSE.txt                RingMouse license
    THIRD-PARTY-NOTICES.md     licenses of the bundled components
    SHA256SUMS.txt             SHA-256 checksums of all files above

.EXAMPLE
  .\build.ps1 -Target Publish
  .\tools\package-release.ps1 -OutputDirectory dist
#>
param(
    [string]$OutputDirectory = 'dist'
)

$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    foreach ($required in 'publish\RingMouse\RingMouse.exe', 'publish\probe\ringmouse-probe.exe', 'publish\RingMouse-uiAccess\RingMouse.exe',
        'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        if (-not (Test-Path $required)) { throw "Missing: $required - run .\build.ps1 -Target Publish first." }
    }

    Remove-Item -Recurse -Force $OutputDirectory -ErrorAction SilentlyContinue
    New-Item -ItemType Directory $OutputDirectory | Out-Null
    Copy-Item 'publish\RingMouse\RingMouse.exe' $OutputDirectory
    Copy-Item 'publish\probe\ringmouse-probe.exe' $OutputDirectory
    Copy-Item 'LICENSE' (Join-Path $OutputDirectory 'LICENSE.txt')
    Copy-Item 'THIRD-PARTY-NOTICES.md' $OutputDirectory

    # uiAccess-Paket: Ordnerstruktur wie im Repo, damit tools\install-uiaccess.ps1 seine Standardquelle findet
    $stage = Join-Path ([IO.Path]::GetTempPath()) ('ringmouse-uiaccess-' + [guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory "$stage\publish", "$stage\tools" | Out-Null
        Copy-Item 'publish\RingMouse-uiAccess' "$stage\publish\RingMouse-uiAccess" -Recurse
        Copy-Item 'tools\install-uiaccess.ps1' "$stage\tools\"
        Copy-Item 'LICENSE' "$stage\LICENSE.txt"
        Copy-Item 'THIRD-PARTY-NOTICES.md' $stage
        Compress-Archive -Path "$stage\*" -DestinationPath (Join-Path $OutputDirectory 'RingMouse-uiAccess.zip')
    }
    finally {
        Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
    }

    $sums = Get-ChildItem $OutputDirectory -File | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') $sums -Encoding ascii
    Get-ChildItem $OutputDirectory -File | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
}
finally {
    Pop-Location
}
