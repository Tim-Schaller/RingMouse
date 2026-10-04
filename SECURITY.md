# Security Policy

RingMouse runs locally, drives Logitech mice over HID++ and opens **no network
connections** (no telemetry, no update check, no account). Everything stays on
your PC. This document describes the trust model, the known residual risks and
how to report a vulnerability.

## Reporting a vulnerability

Please report security issues **privately**, not as a public issue:

- Use GitHub's *Report a vulnerability* button under the repository's **Security**
  tab (Private vulnerability reporting), or
- open a regular issue that only says you found a security problem and asks for a
  private channel — without the details.

Please include the affected version, your OS, and steps to reproduce. You will
normally get a first response within a few days.

## Trust model

### The configuration is executable

`%APPDATA%\RingMouse\config.json` is the single source of truth and is **reloaded
automatically** whenever it changes. A configuration can bind actions that run
arbitrary programs (`launch`) or PowerShell (`powershell`, run with
`-ExecutionPolicy Bypass`) when you press a mouse button or pick a ring segment.

**Consequences:**

- Anyone who can write your `config.json` can run code **as you**, the next time
  the triggering button is pressed (or immediately, via another action). This is
  by design — it is your automation — but it means the file is as sensitive as a
  startup script.
- **Only import configurations from a source you trust.** When you import a
  RingMouse configuration or an Options+ ring preset that contains program or
  PowerShell actions, RingMouse shows a warning in the import dialog. Review the
  listed actions before accepting.
- On startup RingMouse restricts the ACL of its data folder
  (`%APPDATA%\RingMouse`) to the current user, `SYSTEM` and `Administrators`, and
  drops inherited permissions. This guards against an over-permissive parent
  folder and against *other* standard accounts on the same PC. It does **not**
  protect against code already running in your own user context (see below).

### uiAccess variant — residual risk

`tools\install-uiaccess.ps1` can install a signed copy to `C:\Program Files\RingMouse`
with `uiAccess="true"`. That lets RingMouse send input to **higher-integrity
(admin) windows** without being elevated itself (it bypasses UIPI). This is useful
if you work with admin windows, but it raises the stakes of the point above:

> An attacker who is **already running in your user session** (medium integrity)
> and can write your `config.json` could use the uiAccess RingMouse as a
> *confused deputy* to drive input into admin windows — a local privilege
> escalation from your account to high integrity.

A per-user ACL cannot prevent this, because the attacker runs with your own
rights. To keep the risk low:

- Install the uiAccess variant **only if you actually need input in admin
  windows**. The normal variant (`asInvoker`) does not carry this risk.
- Treat `config.json` as trusted input: don't import untrusted configurations,
  and don't grant other users write access to your `%APPDATA%\RingMouse`.
- The self-signed code-signing certificate created by the script is added to
  **LocalMachine\Root only** (required for the Authenticode check that uiAccess
  performs), its private key stays non-exportable and is readable by
  administrators only. Remove it (`certlm.msc` → *Trusted Root Certification
  Authorities*) if you uninstall the uiAccess variant.

### Autostart "scheduled task with highest privileges"

Creating this task writes a task XML to `%TEMP%` and registers it with elevated
`schtasks`. After creation RingMouse **reads the registered task back and verifies**
that it runs exactly `RingMouse.exe --autostart`; if it does not, the task is
deleted. This closes the time-of-check/time-of-use window on the temporary file.

### Logs

Logs are written to `%APPDATA%\RingMouse\logs` (kept 14 days) and never leave the
machine. Action logging is **redacted**: snippet text, inline PowerShell commands
and launch arguments are not written to the log (only their type/length). The
optional raw HID++ log (`logs\hidpp-*.log`, off by default) contains device
protocol frames, not keystrokes.

### Device safety

RingMouse only writes **non-persistent** device state (temporary button diversion
with the persist bit cleared, non-persistent DPI that is validated against the
device's supported list and clamped to 50–32000). It never writes firmware,
device-reset or out-of-band registers. Its own diversions are reset on exit.

## Release integrity

Release binaries are published on the GitHub Releases page together with a
`SHA256SUMS.txt`. The standard `RingMouse.exe` is **not Authenticode-signed**, so
Windows SmartScreen may warn on first run. Verify your download against the
published checksum before running it, e.g.:

```powershell
Get-FileHash .\RingMouse.exe -Algorithm SHA256
```

and compare the hash with `SHA256SUMS.txt` from the same release. (The uiAccess
variant is signed locally on your machine by `install-uiaccess.ps1`.)

## Supported versions

Security fixes are applied to the latest release. Please update to the newest
version before reporting an issue.
