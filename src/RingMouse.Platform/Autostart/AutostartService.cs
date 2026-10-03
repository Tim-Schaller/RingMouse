using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RingMouse.Core.Config;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Platform.Autostart;

public sealed record AutostartResult(bool Success, string Message);

/// <summary>
/// Autostart per HKCU\...\Run (normale Rechte) oder als Aufgabe "Mit höchsten Privilegien" bei Anmeldung und
/// beim Entsperren (holt ein beendetes/abgestürztes RingMouse zurück; läuft es schon, beendet sich der Start still).
/// Das Anlegen/Löschen der Aufgabe braucht einmal eine UAC-Bestätigung (schtasks.exe per "runas").
/// </summary>
public sealed class AutostartService(ILogger? logger = null)
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "RingMouse";
    public const string TaskName = "RingMouse";

    public bool RunEntryExists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>true/false = sicher, null = unbekannt (z.B. keine Leserechte auf die Aufgabe).</summary>
    public bool? TaskExists()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            p.WaitForExit(5000);
            if (p.ExitCode == 0) return true;
            var err = p.StandardError.ReadToEnd();
            return err.Contains("Zugriff", StringComparison.OrdinalIgnoreCase) || err.Contains("access", StringComparison.OrdinalIgnoreCase)
                ? null
                : false;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "schtasks /Query failed");
            return null;
        }
    }

    public AutostartMode DetectCurrent() =>
        RunEntryExists() ? AutostartMode.Run : TaskExists() == true ? AutostartMode.Task : AutostartMode.Off;

    public AutostartResult Apply(AutostartMode mode, string exePath)
    {
        try
        {
            switch (mode)
            {
                case AutostartMode.Off:
                    RemoveRunEntry();
                    if (TaskExists() != false && !DeleteTask())
                        return new(false, L("Could not delete the scheduled task (UAC cancelled?).", "Aufgabe konnte nicht gelöscht werden (UAC abgebrochen?)."));
                    return new(true, L("Autostart disabled.", "Autostart deaktiviert."));

                case AutostartMode.Run:
                    SetRunEntry(exePath);
                    if (TaskExists() == true) DeleteTask();
                    return new(true, L("Autostart set up via HKCU\\...\\Run.", "Autostart über HKCU\\...\\Run eingerichtet."));

                case AutostartMode.Task:
                    if (!CreateTask(exePath))
                        return new(false, L("Could not create the scheduled task (UAC cancelled?).", "Aufgabe konnte nicht angelegt werden (UAC abgebrochen?)."));
                    RemoveRunEntry();
                    return new(true, L("Autostart set up as a scheduled task with highest privileges.", "Autostart als Aufgabe mit höchsten Privilegien eingerichtet."));
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Could not set autostart to {Mode}", mode);
            return new(false, ex.Message);
        }
        return new(false, L("Unknown mode.", "Unbekannter Modus."));
    }

    public void SetRunEntry(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{exePath}\" --autostart", RegistryValueKind.String);
    }

    public void RemoveRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public bool CreateTask(string exePath)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"RingMouse-task-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(exePath, WindowsIdentity.GetCurrent().Name), Encoding.Unicode);
            return RunElevated("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* egal */ }
        }
    }

    public bool DeleteTask() => RunElevated("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");

    /// <summary>
    /// Aufgaben-XML: Anmeldetrigger für den aktuellen Benutzer, interaktiv, höchste Rechte,
    /// normale Priorität (Standard 7 wäre "niedriger als normal") und ohne 72-h-Laufzeitlimit.
    /// </summary>
    public static string BuildTaskXml(string exePath, string userId)
    {
        var user = SecurityElement.Escape(userId);
        var exe = SecurityElement.Escape(exePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "");
        var description = SecurityElement.Escape(L("RingMouse – Actions Ring and battery indicator for Logitech mice",
            "RingMouse – Actions Ring und Akku-Anzeige für Logitech-Mäuse"));
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>{description}</Description>
                <URI>\{TaskName}</URI>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
                <SessionStateChangeTrigger>
                  <Enabled>true</Enabled>
                  <StateChange>SessionUnlock</StateChange>
                  <UserId>{user}</UserId>
                  <Delay>PT3S</Delay>
                </SessionStateChangeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <Arguments>--autostart</Arguments>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private bool RunElevated(string file, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p is null) return false;
            p.WaitForExit(30000);
            var ok = p.HasExited && p.ExitCode == 0;
            logger?.LogInformation("{File} {Args} → Exit {Code}", file, arguments.Split(' ')[0], p.HasExited ? p.ExitCode : -1);
            return ok;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            logger?.LogInformation("UAC prompt cancelled");
            return false;
        }
    }
}
