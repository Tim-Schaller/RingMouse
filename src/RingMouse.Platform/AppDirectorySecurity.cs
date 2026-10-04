using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace RingMouse.Platform;

/// <summary>
/// Härtet den RingMouse-Datenordner (%APPDATA%\RingMouse): schaltet die Vererbung ab und lässt nur den aktuellen
/// Benutzer, SYSTEM und Administratoren zu. Die config.json steuert ausführbare Aktionen (Programme/PowerShell);
/// ein zu breiter Schreibzugriff – etwa durch eine fehlkonfigurierte Vererbung eines übergeordneten Ordners oder
/// durch ein anderes Standardkonto am selben PC – wäre damit ein Angriffsvektor. Best effort; Fehler sind harmlos.
///
/// Hinweis: Gegen einen Angreifer, der bereits <em>im selben Benutzerkontext</em> läuft, schützt eine ACL nicht
/// (er hat dieselben Rechte). Dieses Restrisiko ist dokumentiert (SECURITY.md).
/// </summary>
public static class AppDirectorySecurity
{
    [SupportedOSPlatform("windows")]
    public static void Harden(string directory, ILogger? logger = null)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            if (!dir.Exists) return;

            var self = WindowsIdentity.GetCurrent().User;
            if (self is null)
            {
                logger?.LogDebug("Skipping ACL hardening: current user SID unavailable");
                return; // ohne den eigenen Benutzer keine ACL setzen – sonst Selbstaussperrung
            }

            var security = new DirectorySecurity();
            // Vererbung abschalten und geerbte Regeln NICHT übernehmen – die Zugriffsrechte werden vollständig neu gesetzt.
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            const FileSystemRights rights = FileSystemRights.FullControl;
            const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

            security.AddAccessRule(new FileSystemAccessRule(self, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
            foreach (var wellKnown in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(wellKnown, null), rights, inheritance,
                    PropagationFlags.None, AccessControlType.Allow));

            dir.SetAccessControl(security);
            logger?.LogDebug("Hardened ACL of {Directory}", directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
                                       or ArgumentException or InvalidOperationException)
        {
            // Best effort: schlägt es fehl, bleibt die (ohnehin benutzerprivate) Standard-ACL von %APPDATA% erhalten.
            logger?.LogDebug(ex, "Could not harden the ACL of {Directory}", directory);
        }
    }
}
