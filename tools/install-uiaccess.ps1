<#
.SYNOPSIS
  Installiert RingMouse mit uiAccess nach C:\Program Files\RingMouse und signiert die Exe.

.DESCRIPTION
  Warum: Fenster mit Adminrechten (bei dir: Prozesse des Admin-Kontos) haben eine höhere Integritätsstufe.
  Windows blockiert dorthin Eingaben (SendInput) und Low-Level-Hooks normaler Prozesse (UIPI).
  Eine Exe mit uiAccess="true" darf das – ohne selbst Adminrechte zu haben –, aber nur wenn sie
    1. Authenticode-signiert ist (Zertifikat muss auf dem Rechner vertrauenswürdig sein) und
    2. in einem geschützten Ordner liegt (z.B. C:\Program Files\RingMouse).

  Das Skript
    - verwendet ein vorhandenes Codesignatur-Zertifikat (-Thumbprint, z.B. aus der Firmen-PKI) oder erstellt
      ein lokales, selbstsigniertes (nicht exportierbar) und vertraut ihm auf diesem Rechner,
    - kopiert publish\RingMouse-uiAccess nach C:\Program Files\RingMouse,
    - signiert RingMouse.exe und prüft die Signatur.

  Muss als Administrator laufen. Autostart danach in RingMouse selbst einschalten ("Mit Windows starten"),
  NICHT hier – das Skript läuft ggf. unter einem anderen (Admin-)Konto mit einer anderen HKCU.

.EXAMPLE
  # vorher: .\build.ps1 -Target Publish
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
if (-not (Test-Path $exeSource)) { throw "Nicht gefunden: $exeSource – zuerst .\build.ps1 -Target Publish ausführen." }

# 1) Zertifikat
if ($Thumbprint) {
    $cert = Get-ChildItem Cert:\LocalMachine\My, Cert:\CurrentUser\My | Where-Object Thumbprint -eq $Thumbprint | Select-Object -First 1
    if (-not $cert) { throw "Zertifikat $Thumbprint nicht gefunden." }
}
else {
    $cert = Get-ChildItem Cert:\LocalMachine\My -CodeSigningCert | Where-Object Subject -eq $Subject | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        Write-Host "Erzeuge lokales Codesignatur-Zertifikat '$Subject' …" -ForegroundColor Cyan
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
            Write-Host "Zertifikat in LocalMachine\$storeName aufgenommen." -ForegroundColor Cyan
        }
        $store.Close()
    }
}

# 2) Laufende Instanz beenden (setzt ihre Tastenumleitungen zurück)
$running = Get-Process RingMouse -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Beende laufendes RingMouse …'
    # Sauber per --exit der laufenden Exe (setzt die Umleitungen zurück). Aus einem anderen Konto fehlt dafür evtl.
    # der Zugriff – dann hart beenden (temporäre Umleitungen setzt die neue Instanz ohnehin neu).
    try { if ($running[0].Path) { & $running[0].Path --exit | Out-Null } } catch { }
    Start-Sleep -Milliseconds 500
    Get-Process RingMouse -ErrorAction SilentlyContinue | Stop-Process -Force
}

# 3) Kopieren
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force
$exe = Join-Path $Target 'RingMouse.exe'

# 4) Signieren und prüfen
$result = Set-AuthenticodeSignature -FilePath $exe -Certificate $cert -HashAlgorithm SHA256
if ($result.Status -ne 'Valid') { throw "Signatur fehlgeschlagen: $($result.Status) $($result.StatusMessage)" }
$check = Get-AuthenticodeSignature -FilePath $exe
Write-Host "Signatur: $($check.Status) ($($check.SignerCertificate.Subject))" -ForegroundColor Green

Write-Host ""
Write-Host "Fertig: $exe" -ForegroundColor Green
Write-Host "Nächste Schritte (als dein normales Konto):"
Write-Host "  1. Alte RingMouse.exe-Autostarts entfernen bzw. RingMouse aus $Target starten."
Write-Host "  2. Einstellungen → Allgemein → 'Mit Windows starten' wählen und speichern."
Write-Host "  3. Einstellungen → Allgemein → Info zeigt 'uiAccess ja'."
