<#
.SYNOPSIS
  Installs RingMouse with uiAccess to C:\Program Files\RingMouse and signs the exe.

.DESCRIPTION
  Why: elevated windows (e.g. processes of a separate admin account) run at a higher integrity level.
  Windows blocks input (SendInput) and low-level hooks from normal processes to them (UIPI).
  An exe with uiAccess="true" may do this (without having admin rights itself), but only if it
    1. is Authenticode-signed (the certificate must be trusted on the machine) and
    2. is located in a protected folder (e.g. C:\Program Files\RingMouse).

  The script
    - uses an existing code signing certificate (-Thumbprint, e.g. from the company PKI) or creates
      a local, self-signed one (not exportable) and trusts it on this machine,
    - copies publish\RingMouse-uiAccess to C:\Program Files\RingMouse (from the release zip: extract it and run
      tools\install-uiaccess.ps1 from the extracted folder),
    - signs RingMouse.exe and verifies the signature.

  Must run as administrator. Enable autostart afterwards in RingMouse itself ("Start with Windows"),
  NOT here - the script may run under a different (admin) account with a different HKCU.

.EXAMPLE
  # first: .\build.ps1 -Target Publish
  Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -File "C:\...\RingMouse\tools\install-uiaccess.ps1"'
#>
#Requires -RunAsAdministrator
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\publish\RingMouse-uiAccess'),
    [string]$Target = (Join-Path $env:ProgramFiles 'RingMouse'),
    [string]$Thumbprint,
    [string]$Subject = 'CN=RingMouse Code Signing (lokal)'
)

$ErrorActionPreference = 'Stop'

$exeSource = Join-Path $Source 'RingMouse.exe'
if (-not (Test-Path $exeSource)) { throw "Not found: $exeSource - run .\build.ps1 -Target Publish first." }

# 1) Zertifikat
if ($Thumbprint) {
    $cert = Get-ChildItem Cert:\LocalMachine\My, Cert:\CurrentUser\My | Where-Object Thumbprint -eq $Thumbprint | Select-Object -First 1
    if (-not $cert) { throw "Certificate $Thumbprint not found." }
}
else {
    $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert | Where-Object Subject -eq $Subject | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Creating local code signing certificate '$Subject'..." -ForegroundColor Cyan
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\LocalMachine\My `
            -KeyExportPolicy NonExportable -KeyUsage DigitalSignature -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
    }
    # Nur diesem Rechner das (öffentliche) Zertifikat als vertrauenswürdig bekannt machen
    foreach ($storeName in 'Root', 'TrustedPublisher') {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, 'LocalMachine')
        $store.Open('ReadWrite')
        if (-not ($store.Certificates | Where-Object Thumbprint -eq $cert.Thumbprint)) {
            $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, $cert.Export('Cert'))
            $store.Add($public)
            Write-Host "Certificate added to LocalMachine\$storeName." -ForegroundColor Cyan
        }
        $store.Close()
    }
}

# 2) Laufende Instanz beenden (setzt ihre Tastenumleitungen zurück)
$running = Get-Process RingMouse -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping running RingMouse...'
    # Sauber per --exit der laufenden Exe (setzt die Umleitungen zurück). Aus einem anderen Konto fehlt dafür evtl.
    # der Zugriff – dann hart beenden (temporäre Umleitungen setzt die neue Instanz ohnehin neu).
    try { if ($running[0].Path) { & $running[0].Path --exit | Out-Null } } catch { }
    Start-Sleep -Milliseconds 500
    Get-Process RingMouse -ErrorAction SilentlyContinue | Stop-Process -Force
}

# 3) Kopieren
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
# Aus dem Internet geladene Release-Dateien tragen eine Zone-Markierung, die beim Kopieren mitwandert
Get-ChildItem $Target -Recurse -File | Unblock-File
$exe = Join-Path $Target 'RingMouse.exe'

# 4) Signieren und prüfen
$result = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
if ($result.Status -ne 'Valid') { throw "Signing failed: $($result.Status) $($result.StatusMessage)" }
$check = Get-AuthenticodeSignature -FilePath $exe
Write-Host "Signature: $($check.Status) ($($check.SignerCertificate.Subject))" -ForegroundColor Green

Write-Host ""
Write-Host "Done: $exe" -ForegroundColor Green
Write-Host "Next steps (as your normal account):"
Write-Host "  1. Remove old RingMouse.exe autostart entries and start RingMouse from $Target."
Write-Host "  2. Settings -> General -> select 'Start with Windows' and save."
Write-Host "  3. Settings -> General -> Info shows 'uiAccess yes'."
