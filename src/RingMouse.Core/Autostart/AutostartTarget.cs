namespace RingMouse.Core.Autostart;

/// <summary>Wohin ein eingerichteter Autostart (Run-Eintrag, Aufgabe, Startmenü-Verknüpfung) zeigt – verglichen mit der laufenden Exe.</summary>
public enum AutostartTargetState
{
    /// <summary>Startet genau diese Exe.</summary>
    Current,

    /// <summary>Die eingetragene Exe gibt es nicht mehr (z.B. nach einem Umzug) – der Start läuft ins Leere.</summary>
    Missing,

    /// <summary>Startet eine andere, vorhandene RingMouse-Kopie – bleibt unangetastet.</summary>
    OtherCopy,

    /// <summary>Nicht lesbar (z.B. keine Rechte auf die Aufgabe).</summary>
    Unknown,
}

/// <summary>Reine Pfad-Logik für Autostart und Startmenü – ohne Registry, Aufgabenplanung oder Dateisystem.</summary>
public static class AutostartTarget
{
    /// <summary>
    /// Exe-Pfad aus einer Befehlszeile wie <c>"C:\…\RingMouse.exe" --autostart</c>; ohne Anführungszeichen bis
    /// einschließlich ".exe" (Pfade mit Leerzeichen), sonst bis zum ersten Leerzeichen. null, wenn nichts erkennbar ist.
    /// </summary>
    public static string? ExeFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var s = commandLine.Trim();
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end > 1 ? s[1..end] : null;
        }
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return s[..(exe + 4)];
        var space = s.IndexOf(' ');
        return space > 0 ? s[..space] : s;
    }

    /// <summary>Gleicher Pfad nach Normalisierung (Groß-/Kleinschreibung, Anführungszeichen, relative Teile).</summary>
    public static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a.Trim().Trim('"')), Path.GetFullPath(b.Trim().Trim('"')),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Vergleicht die eingetragene Exe mit der laufenden. Eine nicht erkennbare Eintragung (null/leer) gilt als
    /// <see cref="AutostartTargetState.Missing"/>: Sie startet nichts und darf repariert werden.
    /// </summary>
    public static AutostartTargetState Classify(string? registeredExe, string currentExe, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(registeredExe)) return AutostartTargetState.Missing;
        if (SamePath(registeredExe, currentExe)) return AutostartTargetState.Current;
        return fileExists(registeredExe.Trim().Trim('"')) ? AutostartTargetState.OtherCopy : AutostartTargetState.Missing;
    }
}
