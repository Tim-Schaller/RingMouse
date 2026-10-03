using System.Diagnostics;
using System.Threading;

namespace RingMouse.App;

public static class Program
{
    public const string MutexName = @"Local\RingMouse.SingleInstance";
    public const string ActivateEventName = @"Local\RingMouse.Activate";
    public const string ExitEventName = @"Local\RingMouse.Exit";

    /// <summary>So lange nach Abmelde-/Herunterfahr-Beginn warten, bevor ein Abbruch angenommen wird.</summary>
    private static readonly TimeSpan SessionEndGrace = TimeSpan.FromSeconds(20);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--exit", StringComparer.OrdinalIgnoreCase)) return ExitRunningInstance();
        if (args.Contains("--import-report", StringComparer.OrdinalIgnoreCase)) return WriteImportReport(args);
        // Diagnose ohne Gerät – darf neben einer laufenden Instanz laufen
        if (args.Contains("--render-ui", StringComparer.OrdinalIgnoreCase)) return new RingMouseApplication(args).Run();

        Logging.StartupTrace.WriteEnvironment(args);
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);
        if (!isFirst)
        {
            Logging.StartupTrace.Write("second instance – exiting");
            // Zweite Instanz: bei einem Autostart (Anmeldung/Entsperren) still beenden, sonst die laufende Instanz
            // ihre Einstellungen öffnen lassen.
            if (!args.Contains("--autostart", StringComparer.OrdinalIgnoreCase) &&
                EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
            {
                activate.Set();
                activate.Dispose();
            }
            return 0;
        }

        var app = new RingMouseApplication(args);
        var code = app.Run();
        if (app.EndedBySessionEnding) RestartIfSessionContinues(mutex);
        else if (app.RestartRequested) StartNewInstance(mutex, "--settings");
        GC.KeepAlive(mutex);
        return code;
    }

    /// <summary>
    /// "--import-report &lt;datei&gt;": was ein Import aus der installierten Logi Options+ ergäbe (je Maus Bericht und
    /// Teil-Config) als Textdatei – ändert nichts, zur Kontrolle und für Fehlerberichte.
    /// </summary>
    private static int WriteImportReport(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--import-report", StringComparison.OrdinalIgnoreCase));
        var path = index + 1 < args.Length ? args[index + 1] : "ringmouse-import-report.txt";
        using var importer = Platform.Import.OptionsPlusFiles.Load();
        var text = new System.Text.StringBuilder();
        text.AppendLine($"RingMouse import report · {DateTime.Now:yyyy-MM-dd HH:mm}");
        text.AppendLine($"Options+ settings: {Platform.Import.OptionsPlusFiles.SettingsDatabase}");
        text.AppendLine($"Actions Ring profiles: {Platform.Import.OptionsPlusFiles.RingProfilesRoot}");
        text.AppendLine($"Mice: {string.Join(", ", importer.Mice.Select(m => $"{m.DisplayName} ({m.SlotPrefix})"))}");
        foreach (var prefix in importer.Mice.Count == 0 ? [null] : importer.Mice.Select(m => (string?)m.SlotPrefix))
        {
            var result = importer.Build(prefix);
            text.AppendLine().AppendLine($"=== {prefix ?? "(no mouse)"} · parts: {result.Available}");
            foreach (var note in result.Notes)
                text.AppendLine((note.Kind == Core.Import.ImportNoteKind.Imported ? "  + " : "  - ") + note.Text);
            text.AppendLine(Core.Config.ConfigSerializer.Serialize(result.Config));
        }
        System.IO.File.WriteAllText(path, text.ToString(), new System.Text.UTF8Encoding(false));
        return 0;
    }

    /// <summary>Gewünschter Neustart (z.B. Sprachwechsel): Sperre freigeben und eine neue Instanz starten.</summary>
    private static void StartNewInstance(Mutex mutex, string arguments)
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Logging.StartupTrace.Write($"Restart failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "--exit": laufende Instanz sauber beenden (Tastenumleitungen zurücksetzen), z.B. vor einem Update.
    /// Exit-Code 0 = beendet bzw. lief nicht, 1 = reagiert nicht, 2 = kein Zugriff (andere Sitzung/anderes Konto).
    /// </summary>
    private static int ExitRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exit)) return 0;
            using (exit) exit.Set();
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }

        // Beendet ist sie, wenn ihre Einzelinstanz-Sperre verschwindet.
        try
        {
            for (var i = 0; i < 80; i++)
            {
                if (!Mutex.TryOpenExisting(MutexName, out var running)) return 0;
                running.Dispose();
                Thread.Sleep(100);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return 2;
        }
        return 1;
    }

    /// <summary>
    /// Beim Abmelden/Herunterfahren beendet Windows den Prozess in wenigen Sekunden. Läuft er danach noch, wurde das
    /// Abmelden abgebrochen (z.B. "Abbrechen" wegen ungespeicherter Daten einer anderen App) – dann neu starten,
    /// statt ohne RingMouse weiterzuarbeiten.
    /// </summary>
    private static void RestartIfSessionContinues(Mutex mutex)
    {
        // Die neue Instanz muss die Einzelinstanz-Sperre bekommen.
        mutex.ReleaseMutex();
        mutex.Dispose();
        Thread.Sleep(SessionEndGrace);
        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe, "--autostart") { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // Sitzung endet doch – nichts zu tun
        }
    }
}
