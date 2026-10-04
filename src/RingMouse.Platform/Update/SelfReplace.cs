namespace RingMouse.Platform.Update;

/// <summary>
/// Ersetzt die einzelne RingMouse.exe ohne Installer: Eine laufende Exe lässt sich unter Windows umbenennen (aber
/// nicht überschreiben); danach ist ihr Pfad für die neue Datei frei. Anschließend startet RingMouse sich über das
/// übliche Neustart-Muster neu (Program.Main), und der nächste Start räumt die zurückgelassene .old-Datei weg.
/// </summary>
public static class SelfReplace
{
    public static string OldPath(string installPath) => installPath + ".old";

    /// <summary>true, wenn sich diese Installation selbst ersetzen lässt: Datei ist eine .exe in einem beschreibbaren Ordner.</summary>
    public static bool CanReplace(string? installPath)
    {
        try
        {
            if (string.IsNullOrEmpty(installPath) || !installPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            var dir = Path.GetDirectoryName(installPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".ringmouse-write-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ersetzt die (laufende) Exe am <paramref name="installPath"/> durch <paramref name="newExe"/>. Schlägt das
    /// Kopieren fehl, wird die alte Exe zurückbenannt, damit die Installation funktionsfähig bleibt. Wirft bei Fehler.
    /// </summary>
    public static void Replace(string installPath, string newExe)
    {
        var old = OldPath(installPath);
        try { File.Delete(old); } catch { /* evtl. noch gesperrt – das folgende Move überschreibt */ }
        File.Move(installPath, old, overwrite: true);
        try
        {
            File.Copy(newExe, installPath, overwrite: true);
        }
        catch
        {
            try { File.Move(old, installPath, overwrite: true); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Entfernt die nach einem Update zurückgelassene .old-Datei (best effort).</summary>
    public static void CleanupOld(string installPath)
    {
        try { File.Delete(OldPath(installPath)); } catch { /* beim nächsten Start erneut versucht */ }
    }
}
