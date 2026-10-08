using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using RingMouse.Core.Autostart;
using RingMouse.Core.Config;
using static RingMouse.Core.Localization.Lang;

namespace RingMouse.Platform.Autostart;

public sealed record AutostartResult(bool Success, string Message);

/// <summary>Eingerichteter Autostart (Run-Eintrag hat Vorrang, wie beim Start durch Windows) und wohin er zeigt.</summary>
public sealed record AutostartRegistration(AutostartMode Mode, string? Exe, AutostartTargetState State);

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

    public bool RunEntryExists() => RunEntryCommand() is not null;

    /// <summary>Befehlszeile des Run-Eintrags; null = keiner.</summary>
    public string? RunEntryCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    /// <summary>
    /// Was eingerichtet ist und ob es <paramref name="exePath"/> startet – z.B. zeigt ein Autostart nach dem Verschieben
    /// der Exe ins Leere (<see cref="AutostartTargetState.Missing"/>).
    /// </summary>
    public AutostartRegistration Inspect(string exePath)
    {
        if (RunEntryCommand() is { } command)
        {
            var exe = AutostartTarget.ExeFromCommandLine(Environment.ExpandEnvironmentVariables(command));
            return new(AutostartMode.Run, exe, AutostartTarget.Classify(exe, exePath, File.Exists));
        }
        if (ReadTaskAction() is { } action)
        {
            var exe = action.Command is null ? null : Environment.ExpandEnvironmentVariables(action.Command.Trim().Trim('"'));
            return new(AutostartMode.Task, exe, AutostartTarget.Classify(exe, exePath, File.Exists));
        }
        return TaskExists() == false
            ? new(AutostartMode.Off, null, AutostartTargetState.Current)
            : new(AutostartMode.Task, null, AutostartTargetState.Unknown);
    }

    /// <summary>true/false = sicher, null = unbekannt (z.B. keine Leserechte auf die Aufgabe).</summary>
    public bool? TaskExists()
    {
        try
        {
            return ReadTaskXml() is not null;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not query the scheduled task");
            return null;
        }
    }

    /// <summary>
    /// XML der registrierten Aufgabe über die COM-API der Aufgabenplanung (Schedule.Service) – exakt in Unicode. Die
    /// Textausgabe von "schtasks /Query /XML" kommt umgeleitet in der OEM-Codepage (trotz encoding="UTF-16") und
    /// verliert Zeichen außerhalb davon, z.B. in Benutzernamen. null = keine Aufgabe; wirft z.B. ohne Leserechte.
    /// </summary>
    private static string? ReadTaskXml()
    {
        const int FileNotFound = unchecked((int)0x80070002);
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            try
            {
                return (string)service.GetFolder("\\").GetTask(TaskName).Xml;
            }
            catch (Exception ex) when (ex.HResult == FileNotFound)
            {
                return null;
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

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
            if (!RunElevated("schtasks.exe", $"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F")) return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* egal */ }
        }

        // Die XML lag kurz in %TEMP% und wurde mit Adminrechten (runas) gelesen. Könnte ein anderer Prozess sie in
        // diesem Fenster ausgetauscht haben, liefe eine fremde Aufgabe mit höchsten Rechten. Darum die registrierte
        // Aufgabe zurücklesen und prüfen, dass sie wirklich unsere Exe mit "--autostart" startet – sonst entfernen.
        if (TaskMatches(exePath)) return true;
        logger?.LogError("Registered scheduled task does not match the expected command – deleting it.");
        DeleteTask();
        return false;
    }

    /// <summary>Liest die registrierte Aufgabe zurück; true nur, wenn ihre Aktion genau <paramref name="exePath"/> --autostart ist.</summary>
    private bool TaskMatches(string exePath) =>
        ReadTaskAction() is { } action && action.Arguments?.Trim() == "--autostart" && AutostartTarget.SamePath(action.Command, exePath);

    /// <summary>Befehl und Argumente der registrierten Aufgabe; null = keine Aufgabe oder nicht lesbar.</summary>
    private (string? Command, string? Arguments)? ReadTaskAction()
    {
        try
        {
            if (ReadTaskXml() is not { } xml) return null;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var exec = XDocument.Parse(xml).Descendants(ns + "Exec").FirstOrDefault();
            return (exec?.Element(ns + "Command")?.Value, exec?.Element(ns + "Arguments")?.Value);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not read the scheduled task");
            return null;
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
